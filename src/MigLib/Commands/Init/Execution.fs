module internal MigLib.Commands.Init.Execution

open System
open System.IO
open System.Threading.Tasks

open MigLib.Commands.Types
open MigLib.Commands.Init.SchemaInit
open MigLib.Commands.Resolution.ProjectState
open MigLib.Util

let runInitWithSchema (targetSchema: SqlFile) (newDbPath: string) : Task<Result<InitResult, MigError>> =
  task {
    try
      if File.Exists newDbPath then
        return Error(MigError.Regular $"Database already exists: {Path.GetFullPath newDbPath}")
      else
        let newDirectory = Path.GetDirectoryName newDbPath

        if not (String.IsNullOrWhiteSpace newDirectory) then
          Directory.CreateDirectory newDirectory |> ignore

        use newConnection = Sqlite.openConnection newDbPath
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
  taskResult {
    let! (projectState: ResolvedMigProject) = resolveProjectState project

    if File.Exists projectState.targetDbPath then
      return
        { newDbPath = projectState.targetDbPath
          seededRows = 0L }
    else
      let! (initResult: InitResult) = runInitWithSchema project.targetSchema projectState.targetDbPath
      return initResult
  }
