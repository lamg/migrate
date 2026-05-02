module internal MigLib.Commands.Migrate.Discovery

open System.Threading.Tasks
open Microsoft.Data.Sqlite

open MigLib.Commands.Types
open MigLib.Commands.Migrate.SchemaIntrospection
open MigLib.Commands.Resolution.Assemblies
open MigLib.Commands.Resolution.DatabasePaths
open MigLib.Commands.Resolution.GeneratedSchema
open MigLib.Commands.Resolution.Projects
open MigLib.Commands.Resolution.Types
open MigLib.Util

let private sqliteInitialized = lazy (SQLitePCL.Batteries_V2.Init())

let private ensureSqliteInitialized () = sqliteInitialized.Force()

let private openSqliteConnection dbPath =
  ensureSqliteInitialized ()
  let connection = new SqliteConnection($"Data Source={dbPath}")
  connection.Open()
  connection

let resolveMigrationInputs (project: MigProject) : Result<ResolvedGeneratedSchema * ResolvedDatabasePaths, MigError> =
  result {
    let! resolvedProject = resolveProject project
    let! runtimeAssembly = resolveRuntimeAssembly resolvedProject
    let! generatedSchema = resolveGeneratedSchema runtimeAssembly
    let! databasePaths = resolveDatabasePaths generatedSchema
    return generatedSchema, databasePaths
  }

let findOldSchema (reportProgress: ProgReport) (project: MigProject) : Task<Result<SqlFile option, MigError>> =
  task {
    match resolveMigrationInputs project with
    | Error error -> return Error error
    | Ok(_, paths) ->
      match paths.sourceDbPath with
      | None -> return Ok None
      | Some sourceDbPath ->
        do! reportProgress $"Reading source database schema: {sourceDbPath}"
        use connection = openSqliteConnection sourceDbPath
        let! schemaResult = loadSchemaFromDatabase connection
        return schemaResult |> Result.map Some
  }

let prepareNewDb (reportProgress: ProgReport) (project: MigProject) : Task<Result<string, MigError>> =
  failwith "TODO prepareNewDb"
