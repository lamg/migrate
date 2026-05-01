module internal MigLib.Commands.Init.Execution

open System
open System.IO
open System.Threading.Tasks

open MigLib.Commands.Types
open MigLib.Commands.Init.SchemaInit
open MigLib.Commands.Resolution.Assemblies
open MigLib.Commands.Resolution.DatabasePaths
open MigLib.Commands.Resolution.GeneratedSchema
open MigLib.Commands.Resolution.Projects
open MigLib.Util

let private sqliteInitialized = lazy (SQLitePCL.Batteries_V2.Init())

let private ensureSqliteInitialized () = sqliteInitialized.Force()

let private openSqliteConnection dbPath =
  ensureSqliteInitialized ()
  let connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}")
  connection.Open()
  connection

let runInitWithSchema (targetSchema: SqlFile) (newDbPath: string) : Task<Result<InitResult, MigError>> =
  task {
    try
      if File.Exists newDbPath then
        return Error(MigError.Regular $"Database already exists: {Path.GetFullPath newDbPath}")
      else
        let newDirectory = Path.GetDirectoryName newDbPath

        if not (String.IsNullOrWhiteSpace newDirectory) then
          Directory.CreateDirectory newDirectory |> ignore

        use newConnection = openSqliteConnection newDbPath
        let! initResult = initializeDatabaseFromSchemaOnly newConnection targetSchema

        match initResult with
        | Error error -> return Error error
        | Ok seededRows ->
          return
            Ok
              { newDbPath = Path.GetFullPath newDbPath
                seededRows = seededRows }
    with
    | :? Microsoft.Data.Sqlite.SqliteException as ex -> return Error(MigError.Sqlite ex)
    | ex -> return Error(MigError.Other ex)
  }

let init (project: MigProject) : Task<Result<InitResult, MigError>> =
  task {
    let resolvedInputs =
      result {
        let! resolvedProject = resolveProject project
        let! runtimeAssembly = resolveRuntimeAssembly resolvedProject
        let! generatedSchema = resolveGeneratedSchema runtimeAssembly
        let! paths = resolveDatabasePaths generatedSchema
        return generatedSchema, paths
      }

    match resolvedInputs with
    | Error error -> return Error error
    | Ok(generatedSchema, paths) ->
      if File.Exists paths.targetDbPath then
        return
          Ok
            { newDbPath = paths.targetDbPath
              seededRows = 0L }
      else
        return! runInitWithSchema generatedSchema.generatedModule.schema paths.targetDbPath
  }
