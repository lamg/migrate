module migrate.Execution.Exec

open System
open System.IO

open Microsoft.Data.Sqlite
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open migrate.Execution

let rec internal connect dbFile =
  let connStr = $"Data Source={dbFile};Mode=ReadWriteCreate"

  try
    let conn = new SqliteConnection(connStr)
    Ok conn
  with :? SqliteException as e ->
    Error(Types.OpenDbFailed $"Failed opening {dbFile} with {e.Message}")

let internal sqliteMasterStatements (conn: SqliteConnection) =
  use cmd = conn.CreateCommand()
  cmd.CommandText <- "SELECT sql FROM sqlite_master"
  conn.Open()
  let rd = cmd.ExecuteReader()

  [ while rd.Read() do
      match rd.GetValue 0 |> Option.ofObj |> Option.map _.ToString() with
      | Some x -> yield x
      | None -> () ]

let internal dbElements (conn: SqliteConnection) =
  try
    conn
    |> sqliteMasterStatements
    |> List.choose (function
      | s when s.StartsWith "CREATE TABLE sqlite_" || String.IsNullOrWhiteSpace s -> None
      | s -> Some s)
    |> Ok
  with e ->
    Error(Types.ReadSchemaFailed $"{e.Message}")

[<Literal>]
let migrateDbEnvVar = "migrate_db"

let internal getDbFile (dir: DirectoryInfo) =
  dotenv.net.DotEnv.Load()

  let getEnv s =
    Environment.GetEnvironmentVariable s |> Option.ofObj

  match getEnv migrateDbEnvVar with
  | Some x -> x
  | None -> $"{dir.FullName}/{dir.Name}.sqlite"

let internal dbSchema (dbFile: string) =
  result {
    use! conn = connect dbFile
    let! sql = dbElements conn

    let! schema =
      sql
      |> FormatSql.join
      |> fun sql -> FParsecSqlParser.parseSqlFile (conn.Database, sql)
      |> Result.mapError Types.ParsingFailed

    return schema
  }

let internal readFile path =
  try
    path |> File.ReadAllText |> Ok
  with e ->
    Error(Types.ReadFileFailed e.Message)

type SqlSource = { name: string; content: string }

let internal parseSqlFiles (sources: SqlSource list) =
  sources
  |> List.map (fun s -> FParsecSqlParser.parseSqlFile (s.name, s.content))
  |> Solve.splitResult
  |> function
    | xs, [] ->
      xs
      |> List.fold
        (fun (acc: Types.SqlFile) x ->
          { acc with
              tables = acc.tables @ x.tables
              views = acc.views @ x.views
              indexes = acc.indexes @ x.indexes
              inserts = acc.inserts @ x.inserts
              triggers = acc.triggers @ x.triggers })
        Types.emptyFile
      |> Ok
    | _, errs -> errs |> List.map Types.ParsingFailed |> Types.Composed |> Error

let internal readDirSql (dir: DirectoryInfo) =
  dir.EnumerateFiles()
  |> Seq.toList
  |> List.choose (fun f ->
    if f.Extension = ".sql" then
      let sql = f.OpenText().ReadToEnd()
      { name = f.FullName; content = sql } |> Some
    else
      None)

let migrationStatementsForDb (dbFile: string, sources: SqlSource list) =
  result {
    let! expectedSchema = parseSqlFiles sources
    let! dbSchema = dbSchema dbFile
    return! Migration.migration (dbSchema, expectedSchema)
  }

let migrationStatements () =
  let dir = Environment.CurrentDirectory

  result {
    let dir = DirectoryInfo dir
    let dbFile = getDbFile dir
    let sources = readDirSql dir
    return! migrationStatementsForDb (dbFile, sources)
  }

let generateMigrationScript (withColors: bool) =
  result {
    let! statements = migrationStatements ()

    return statements |> FormatSql.formatSeq withColors
  }

let executeMigration (statements: string list) =
  let dir = Environment.CurrentDirectory |> DirectoryInfo
  let dbFile = getDbFile dir

  match connect dbFile with
  | Ok conn ->
    use conn = conn
    conn.Open()
    use txn = conn.BeginTransaction()


    statements
    |> List.fold
      (fun (hasError, i, xs) sql ->
        let step = sql |> FormatSql.format true

        if not hasError then
          try
            use cmd = conn.CreateCommand()
            cmd.Transaction <- txn
            cmd.CommandText <- sql
            cmd.ExecuteNonQuery() |> ignore
            false, i + 1, $"✅ {i}\n{step}\n" :: xs
          with e ->
            let msg =
              match e with
              | :? SqliteException as x -> x.Message
              | _ -> e.Message

            txn.Rollback()
            true, i + 1, $"❌ {i}\n{step}\n{msg}" :: xs
        else
          true, i + 1, $"⚫ {i}\n{step}" :: xs)
      (false, 0, [])
    |> function
      | hasError, _, xs ->
        let xs = List.rev xs

        if not hasError then
          txn.Commit()
          Ok xs
        else
          Error(Types.FailedSteps xs)

  | Error e -> Error e


let internal validatePrimaryKeys (file: Types.SqlFile) : Types.InsertInto list =
  file.inserts
  |> List.filter (fun insert ->
    let table = file.tables |> List.tryFind (fun t -> t.name = insert.table)

    match table with
    | None ->
      eprintfn $"⚠️  Skipping INSERT for table '{insert.table}': table not found"
      false
    | Some t ->
      let hasPk =
        t.columns
        |> List.exists (fun c ->
          c.constraints
          |> List.exists (function
            | Types.PrimaryKey _ -> true
            | _ -> false))
        || t.constraints
           |> List.exists (function
             | Types.PrimaryKey _ -> true
             | _ -> false)

      if not hasPk then
        eprintfn $"⚠️  Skipping INSERT for table '{insert.table}': no primary key"

      hasPk)

let seedStatements () =
  result {
    let dir = Environment.CurrentDirectory |> DirectoryInfo
    let sources = readDirSql dir

    let! file =
      if sources.IsEmpty then
        Error(Types.ReadSchemaFailed "No SQL files found")
      else
        parseSqlFiles sources

    // Validate primary keys and filter
    let validInserts = validatePrimaryKeys file

    // Sort by dependencies
    let sorted, _ = Solve.sortFile file

    // Create a map of table name to sorted position for ordering
    let sortedIndex =
      sorted.sortedRelations
      |> List.indexed
      |> List.map (fun (i, name) -> name, i)
      |> Map.ofList

    // Generate SQL in dependency order, including all inserts
    let statements =
      validInserts
      |> List.sortBy (fun insert -> sortedIndex |> Map.tryFind insert.table |> Option.defaultValue Int32.MaxValue)
      |> List.map GenerateSql.Seed.upsertSql

    return statements
  }

let executeSeed (statements: string list) =
  let dir = Environment.CurrentDirectory |> DirectoryInfo
  let dbFile = getDbFile dir

  match connect dbFile with
  | Ok conn ->
    use conn = conn
    conn.Open()
    use txn = conn.BeginTransaction()

    statements
    |> List.fold
      (fun (hasError, i, xs) sql ->
        let step = sql |> FormatSql.format true

        if not hasError then
          try
            use cmd = conn.CreateCommand()
            cmd.Transaction <- txn
            cmd.CommandText <- sql
            cmd.ExecuteNonQuery() |> ignore
            false, i + 1, $"✅ {i}\n{step}\n" :: xs
          with e ->
            let msg =
              match e with
              | :? SqliteException as x -> x.Message
              | _ -> e.Message

            txn.Rollback()
            true, i + 1, $"❌ {i}\n{step}\n{msg}" :: xs
        else
          true, i + 1, $"⚫ {i}\n{step}" :: xs)
      (false, 0, [])
    |> function
      | hasError, _, xs ->
        let xs = List.rev xs

        if not hasError then
          txn.Commit()
          Ok xs
        else
          Error(Types.FailedSteps xs)

  | Error e -> Error e


let getDbSql withColors =
  let dir = Environment.CurrentDirectory |> DirectoryInfo
  let dbFile = getDbFile dir

  result {
    use! conn = connect dbFile
    let! statements = dbElements conn

    return statements |> FormatSql.formatSeq withColors
  }
