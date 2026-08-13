namespace MigLib

module Migrate =
  open System
  open System.IO
  open System.Threading.Tasks
  open Microsoft.Data.Sqlite
  open MigLib.Sqlite

  let reservedHopFileName = "_migration.sql"

  type LoadedSchema =
    { ExpectedSchema: string
      Migration: string }

  let private ensureParentDirectory (dbPath: string) =
    let directory = Path.GetDirectoryName(Path.GetFullPath dbPath)

    if not (String.IsNullOrWhiteSpace directory) then
      Directory.CreateDirectory directory |> ignore

  let private quoteIdent (name: string) = "[" + name.Replace("]", "]]") + "]"

  let private isReservedHop (relativePath: string) =
    relativePath.Equals(reservedHopFileName, StringComparison.OrdinalIgnoreCase)

  let private relativeSqlPath (root: string) (fullPath: string) =
    Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/')

  /// Load a schema directory: recursive `*.sql`, hop is root `_migration.sql` only.
  let loadSchemaDirectory (schemaDirectory: string) : Result<LoadedSchema, string> =
    if String.IsNullOrWhiteSpace schemaDirectory then
      Error "schema directory is required"
    elif not (Directory.Exists schemaDirectory) then
      Error("schema directory not found: " + schemaDirectory)
    else
      try
        let root = Path.GetFullPath schemaDirectory

        let files =
          Directory.GetFiles(root, "*.sql", SearchOption.AllDirectories)
          |> Array.sortWith (fun a b ->
            String.Compare(relativeSqlPath root a, relativeSqlPath root b, StringComparison.Ordinal))

        let snapshot = ResizeArray<string>()
        let mutable hop = ""

        for path in files do
          let rel = relativeSqlPath root path
          let body = File.ReadAllText path

          if isReservedHop rel then hop <- body else snapshot.Add body

        Ok
          { ExpectedSchema = String.Join("\n", snapshot)
            Migration = hop }
      with ex ->
        Error ex.Message

  let private bindTx (cmd: SqliteCommand) (tx: SqliteTransaction) =
    if not (isNull tx) then
      cmd.Transaction <- tx

  let private tableColumns (conn: SqliteConnection) (tx: SqliteTransaction) (table: string) =
    use cmd = conn.CreateCommand()
    bindTx cmd tx
    cmd.CommandText <- "PRAGMA table_info(" + quoteIdent table + ")"
    use reader = cmd.ExecuteReader()
    let cols = ResizeArray<string>()

    while reader.Read() do
      let name = reader.GetString 1
      let typ = if reader.IsDBNull 2 then "" else reader.GetString 2
      let notnull = reader.GetInt32 3
      let pk = reader.GetInt32 5
      cols.Add(name + ":" + typ + ":" + string notnull + ":" + string pk)

    cols |> List.ofSeq

  let private readCatalog (conn: SqliteConnection) (tx: SqliteTransaction) =
    use cmd = conn.CreateCommand()
    bindTx cmd tx

    cmd.CommandText <-
      """
SELECT [type], [name]
FROM sqlite_master
WHERE [name] NOT LIKE 'sqlite_%'
ORDER BY [type], [name];
"""

    let objects =
      use reader = cmd.ExecuteReader()
      let rows = ResizeArray<string * string>()

      while reader.Read() do
        rows.Add(reader.GetString 0, reader.GetString 1)

      rows |> List.ofSeq

    objects
    |> List.map (fun (typ, name) ->
      if typ = "table" then
        typ, name, String.concat "," (tableColumns conn tx name)
      else
        typ, name, "")

  let private execSql (conn: SqliteConnection) (sql: string) =
    if not (String.IsNullOrWhiteSpace sql) then
      use cmd = conn.CreateCommand()
      cmd.CommandText <- sql
      cmd.ExecuteNonQuery() |> ignore

  let private fromEx (ex: exn) =
    match ex with
    | :? SqliteException as e -> Types.MigError.Sqlite e
    | _ -> Types.MigError.Other ex

  let private ready (dbPath: string) = Types.dbTxn dbPath

  let private catalogOfSql (sql: string) : Result<(string * string * string) list, Types.MigError> =
    let tempPath =
      Path.Combine(Path.GetTempPath(), "mig-expect-" + Guid.NewGuid().ToString "N" + ".sqlite")

    try
      try
        ensureInitialized ()
        use conn = openConnection tempPath
        execSql conn sql
        Ok(readCatalog conn null)
      with ex ->
        Error(fromEx ex)
    finally
      try
        if File.Exists tempPath then
          File.Delete tempPath
      with _ ->
        ()

      for suffix in [ "-wal"; "-shm" ] do
        try
          let side = tempPath + suffix

          if File.Exists side then
            File.Delete side
        with _ ->
          ()

  let private formatCatalog (rows: (string * string * string) list) =
    rows
    |> List.map (fun (typ, name, detail) -> typ + " " + name + " " + detail)
    |> String.concat "\n"

  let private mismatch (expected: (string * string * string) list) (actual: (string * string * string) list) =
    "catalog does not match expected schema.\nExpected:\n"
    + formatCatalog expected
    + "\nActual:\n"
    + formatCatalog actual

  /// Apply snapshot (empty DB) or hop + catalog check.
  let migrate
    (dbPath: string)
    (expectedSchema: string)
    (migrationSql: string)
    : Task<Result<Types.DbTxnBuilder, Types.MigError>> =
    task {
      try
        ensureParentDirectory dbPath
        ensureInitialized ()

        match catalogOfSql expectedSchema with
        | Error e -> return Error e
        | Ok expected ->
          use conn = openConnection dbPath
          let live = readCatalog conn null

          if live = expected then
            return Ok(ready dbPath)
          elif List.isEmpty live then
            execSql conn expectedSchema
            let after = readCatalog conn null

            if after = expected then
              return Ok(ready dbPath)
            else
              return Error(Types.MigError.Message(mismatch expected after))
          elif String.IsNullOrWhiteSpace migrationSql then
            return
              Error(
                Types.MigError.Message "database is not empty and migration SQL is empty; write schema/_migration.sql"
              )
          else
            use tx = conn.BeginTransaction()

            try
              use cmd = conn.CreateCommand()
              cmd.Transaction <- tx
              cmd.CommandText <- migrationSql
              cmd.ExecuteNonQuery() |> ignore

              let after = readCatalog conn tx

              if after = expected then
                tx.Commit()
                return Ok(ready dbPath)
              else
                tx.Rollback()
                return Error(Types.MigError.Message(mismatch expected after))
            with ex ->
              try
                tx.Rollback()
              with _ ->
                ()

              return Error(fromEx ex)
      with ex ->
        return Error(fromEx ex)
    }
