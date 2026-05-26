module internal MigLib.Init.Execution

open System
open System.IO
open System.Threading.Tasks

open MigLib.Types
open MigLib.Init.SchemaInit
open MigLib.Sqlite

let createDatabaseWithSchema (targetSchema: SqlFile) (newDbPath: string) : Task<Result<string, MigError>> =
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

        return initResult |> Result.map (fun _ -> Path.GetFullPath newDbPath)
    with
    | :? Microsoft.Data.Sqlite.SqliteException as ex -> return Error(MigError.Sqlite ex)
    | ex -> return Error(MigError.Other ex)
  }

let init (project: ResolvedProject) : Task<Result<InitResult, MigError>> =
  task {
    if File.Exists project.targetDbPath then
      return
        Ok
        { newDbPath = project.targetDbPath
          seededRows = 0L }
    else
      let! createResult = createDatabaseWithSchema project.targetSchema.schema project.targetDbPath

      match createResult with
      | Error error -> return Error error
      | Ok dbPath ->
        use connection = openConnection dbPath
        let! seedResult = seedDatabase connection project.targetSchema.schema

        return
          seedResult
          |> Result.map (fun seededRows ->
            { newDbPath = dbPath
              seededRows = seededRows })
  }
