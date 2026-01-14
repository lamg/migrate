module migrate.MigrationLog.ExecAndLog

open System
open System.IO

open FsToolkit.ErrorHandling
open migrate.DeclarativeMigrations

open migrate.Execution

let private primaryKeyCol =
  Types.PrimaryKey
    { columns = []
      constraintName = None
      isAutoincrement = false }

let internal migrationLog: Types.CreateTable =
  { name = "migration_log"
    columns =
      [ { name = "created_at"
          columnType = Types.SqlString
          constraints = [ primaryKeyCol ] }
        { name = "message"
          columnType = Types.SqlString
          constraints = [ Types.NotNull; Types.Default(Types.String "") ] } ]
    constraints = []
    queryByAnnotations = [] }

let private nowRFC3339 () =
  DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK")

let internal migrationSteps: Types.CreateTable =
  { name = "migration_step"
    columns =
      [ { name = "log_created_at"
          columnType = Types.SqlString
          constraints = [ Types.NotNull ] }
        { name = "step"
          columnType = Types.SqlString
          constraints = [ Types.NotNull ] } ]
    constraints =
      [ Types.ForeignKey
          { columns = [ "log_created_at" ]
            refTable = "migration_log"
            refColumns = [ "created_at" ] } ]
    queryByAnnotations = [] }

let migrationStatements () =
  result {
    let dir = Environment.CurrentDirectory |> DirectoryInfo
    let! expectedSchema = Exec.readDirSql dir |> Exec.parseSqlFiles

    let expectedWithMigTables =
      { expectedSchema with
          tables = expectedSchema.tables @ [ migrationLog; migrationSteps ] }

    let dbFile = Exec.getDbFile dir
    let! dbSchema = Exec.dbSchema dbFile
    return! Migration.migration (dbSchema, expectedWithMigTables)
  }

let generateMigrationScript (withColors: bool) =
  result {
    let! statements = migrationStatements ()

    return statements |> FormatSql.formatSeq withColors
  }

let executeMigrations (message: string option, statements: string list) =
  let now = nowRFC3339 ()

  let insertLog =
    match message with
    | Some m -> $"INSERT INTO migration_log(message, created_at) VALUES ('{m}', '{now}')"
    | None -> $"INSERT INTO migration_log(created_at) VALUES ('{now}')"

  let escapeString (s: string) = s.Replace("'", "''")

  let values =
    statements
    |> List.map (fun s -> $"('{now}', '{escapeString s}')")
    |> String.concat ",\n"

  let insertSteps =
    $"INSERT INTO migration_step(log_created_at, step) VALUES {values}"

  let insertLogSteps =
    match statements with
    | [] -> []
    | _ -> [ insertLog; insertSteps ]

  result {
    let! res = Exec.executeMigration statements

    return
      match Exec.executeMigration insertLogSteps with
      | Ok _ -> res
      | Error(Types.FailedSteps xs) -> res @ "Successful migration, but inserting the log failed:" :: xs
      | Error e -> res @ [ e.ToString() ]
  }

let public log () =
  let dir = Environment.CurrentDirectory |> DirectoryInfo
  let dbFile = Exec.getDbFile dir

  let greenFragment x =
    let ansiGreen = "\x1b[32m"
    let ansiReset = "\x1b[0m"
    $"%s{ansiGreen}%s{x}%s{ansiReset}"

  match Exec.connect dbFile with
  | Ok conn ->
    use conn = conn
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT message, created_at FROM migration_log"
    let rd = cmd.ExecuteReader()

    [ while rd.Read() do
        let date = greenFragment $"date: {rd.GetString 1}"
        let message = $"message: {rd.GetString 0}"
        yield $"{date}\n{message}" ]
  | Error e -> [ $"{e}" ]

let public showSteps (createdAt: string) =
  let dir = Environment.CurrentDirectory |> DirectoryInfo
  let dbFile = Exec.getDbFile dir

  match Exec.connect dbFile with
  | Ok conn ->
    use conn = conn
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- $"SELECT step FROM migration_step WHERE log_created_at = '{createdAt}'"
    let rd = cmd.ExecuteReader()

    [ while rd.Read() do
        yield rd.GetString 0 ]
  | Error e -> [ $"{e}" ]
