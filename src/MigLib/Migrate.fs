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

  let private listScripts (scriptsDirectory: string) =
    Directory.GetFiles(scriptsDirectory, "*.sql")
    |> Array.sortWith (fun a b ->
      String.Compare(Path.GetFileName a, Path.GetFileName b, StringComparison.OrdinalIgnoreCase))
    |> Array.toList

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

  let private applyScript (conn: SqliteConnection) (scriptPath: string) =
    let scriptName = Path.GetFileName scriptPath
    let sql = File.ReadAllText scriptPath
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

  /// Applies ordered *.sql scripts from <paramref name="scriptsDirectory"/> to the database at <paramref name="dbPath"/>.
  /// Script order is the file name (ordinal ignore-case). Applied scripts are recorded in SchemaVersions.
  let migrateScripts (dbPath: string) (scriptsDirectory: string) : Task<Result<unit, string>> =
    task {
      try
        if String.IsNullOrWhiteSpace scriptsDirectory then
          return Error "migrations directory is required"
        elif not (Directory.Exists scriptsDirectory) then
          return Error("migrations directory not found: " + scriptsDirectory)
        else
          ensureParentDirectory dbPath
          ensureInitialized ()

          use conn = openConnection dbPath
          ensureJournal conn
          let applied = loadApplied conn
          let scripts = listScripts scriptsDirectory

          for path in scripts do
            let name = Path.GetFileName path

            if not (applied.Contains name) then
              applyScript conn path
              applied.Add name |> ignore

          return Ok()
      with ex ->
        return Error ex.Message
    }
