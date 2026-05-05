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
  taskResult {
    if File.Exists project.targetDbPath then
      return! Error(MigError.Regular $"Target database already exists: {Path.GetFullPath project.targetDbPath}")
    else
      do! reportProgress $"Creating target database: {project.targetDbPath}"
      let! (initResult: InitResult) = runInitWithSchema project.targetSchema.schema project.targetDbPath
      return initResult.newDbPath
  }
