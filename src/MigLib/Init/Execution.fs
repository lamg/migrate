module internal MigLib.Init.Execution

open System
open System.IO
open System.Threading.Tasks

open MigLib.Types
open MigLib.Init.SchemaInit
open MigLib.TaskResult
open MigLib.Sqlite

let runInitWithSchema (targetSchema: SqlFile) (newDbPath: string) : Task<Result<InitResult, MigError>> =
  task {
    try
      if File.Exists newDbPath then
        return Error(MigError.Regular $"Database already exists: {Path.GetFullPath newDbPath}")
      else
        let newDirectory = Path.GetDirectoryName newDbPath

        if not (String.IsNullOrWhiteSpace newDirectory) then
          Directory.CreateDirectory newDirectory |> ignore

        use newConnection = openConnection newDbPath
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

let init (project: ResolvedProject) : Task<Result<InitResult, MigError>> =
  taskResult {
    if File.Exists project.targetDbPath then
      return
        { newDbPath = project.targetDbPath
          seededRows = 0L }
    else
      let! (initResult: InitResult) = runInitWithSchema project.targetSchema.schema project.targetDbPath
      return initResult
  }
