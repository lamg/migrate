namespace MigLib

module Migrate =
  open System
  open System.Globalization
  open System.IO
  open System.Threading.Tasks
  open Microsoft.Data.Sqlite
  open MigLib.Sqlite

  let private journalTableSql =
    """
CREATE TABLE IF NOT EXISTS [SchemaVersions] (
  SchemaVersionID INTEGER CONSTRAINT [PK_SchemaVersions_Id] PRIMARY KEY AUTOINCREMENT NOT NULL,
  ScriptName TEXT NOT NULL,
  Applied DATETIME NOT NULL
);
"""

  let private ensureParentDirectory (dbPath: string) =
    let directory = Path.GetDirectoryName(Path.GetFullPath dbPath)

    if not (String.IsNullOrWhiteSpace directory) then
      Directory.CreateDirectory directory |> ignore

  let private ensureJournal (conn: SqliteConnection) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- journalTableSql
    cmd.ExecuteNonQuery() |> ignore

  let private loadApplied (conn: SqliteConnection) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT [ScriptName] FROM [SchemaVersions]"
    use reader = cmd.ExecuteReader()
    let names = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)

    while reader.Read() do
      names.Add(reader.GetString 0) |> ignore

    names

  let private applyNamedScript (conn: SqliteConnection) (scriptName: string) (sql: string) =
    let applied = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)

    use tx = conn.BeginTransaction()

    try
      if not (String.IsNullOrWhiteSpace sql) then
        use exec = conn.CreateCommand()
        exec.Transaction <- tx
        exec.CommandText <- sql
        exec.ExecuteNonQuery() |> ignore

      use journal = conn.CreateCommand()
      journal.Transaction <- tx
      journal.CommandText <- "INSERT INTO [SchemaVersions] ([ScriptName], [Applied]) VALUES (@name, @applied)"

      let nameParam = journal.CreateParameter()
      nameParam.ParameterName <- "@name"
      nameParam.Value <- scriptName
      journal.Parameters.Add nameParam |> ignore

      let appliedParam = journal.CreateParameter()
      appliedParam.ParameterName <- "@applied"
      appliedParam.Value <- applied
      journal.Parameters.Add appliedParam |> ignore

      journal.ExecuteNonQuery() |> ignore
      tx.Commit()
    with _ ->
      try
        tx.Rollback()
      with _ ->
        ()

      reraise ()

  /// Loads ordered *.sql scripts from a directory (dev-time / codegen).
  /// Order is the file name (ordinal ignore-case). Not part of the app runtime surface.
  let loadScriptsFromDirectory (scriptsDirectory: string) : Result<(string * string) list, string> =
    if String.IsNullOrWhiteSpace scriptsDirectory then
      Error "migrations directory is required"
    elif not (Directory.Exists scriptsDirectory) then
      Error("migrations directory not found: " + scriptsDirectory)
    else
      try
        Directory.GetFiles(scriptsDirectory, "*.sql")
        |> Array.sortWith (fun a b ->
          String.Compare(Path.GetFileName a, Path.GetFileName b, StringComparison.OrdinalIgnoreCase))
        |> Array.map (fun path -> Path.GetFileName path, File.ReadAllText path)
        |> Array.toList
        |> Ok
      with ex ->
        Error ex.Message

  /// Applies ordered named scripts to the database at <paramref name="dbPath"/>.
  /// Script names are SchemaVersions keys; list order is apply order. Already-applied names are skipped.
  let migrateScripts (dbPath: string) (scripts: (string * string) list) : Task<Result<unit, string>> =
    task {
      try
        ensureParentDirectory dbPath
        ensureInitialized ()

        use conn = openConnection dbPath
        ensureJournal conn
        let applied = loadApplied conn

        for name, sql in scripts do
          if not (String.IsNullOrWhiteSpace name) && not (applied.Contains name) then
            applyNamedScript conn name sql
            applied.Add name |> ignore

        return Ok()
      with ex ->
        return Error ex.Message
    }
