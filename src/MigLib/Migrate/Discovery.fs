module internal MigLib.Migrate.Discovery

open System.IO
open System.Threading.Tasks

open MigLib.Init.Execution
open MigLib.Types
open MigLib.TaskResult

let findOldSchema (reportProgress: ProgReport) (project: ResolvedProject) : Task<Result<SqlFile option, MigError>> =
  taskResult {
    match project.sourceDbPath, project.sourceDbSchema with
    | Some sourceDbPath, Some sourceSchema ->
      do! reportProgress $"Reading source database schema: {sourceDbPath}"
      return Some sourceSchema
    | _ -> return None
  }

let prepareNewDb (reportProgress: ProgReport) (project: ResolvedProject) : Task<Result<string, MigError>> =
  task {
    if File.Exists project.targetDbPath then
      do! reportProgress "File already exists"
      return Ok project.targetDbPath
    else
      do! reportProgress $"Creating target database: {project.targetDbPath}"

      match project.sourceDbPath with
      | Some _ -> return! createDatabaseWithSchema project.targetSchema.schema project.targetDbPath
      | None ->
        let! createResult =
          createDatabaseWithSchema project.targetSchema.schema project.targetDbPath

        match createResult with
        | Ok path ->
          use conn = MigLib.Sqlite.openConnection path

          let! seedResult =
            MigLib.Init.SchemaInit.seedDatabase conn project.targetSchema.schema

          match seedResult with
          | Ok _ -> return Ok path
          | Error e -> return Error e
        | Error e -> return Error e
  }
