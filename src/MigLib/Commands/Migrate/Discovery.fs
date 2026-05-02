module internal MigLib.Commands.Migrate.Discovery

open System.IO
open System.Threading.Tasks

open MigLib.Commands.Init.Execution
open MigLib.Commands.Resolution.ProjectState
open MigLib.Commands.Types
open MigLib.Util

let resolveMigrationInputs (project: MigProject) : Task<Result<ResolvedMigProject, MigError>> =
  resolveProjectState project

let findOldSchema (reportProgress: ProgReport) (project: MigProject) : Task<Result<SqlFile option, MigError>> =
  taskResult {
    let! (projectState: ResolvedMigProject) = resolveProjectState project

    match projectState.sourceDbPath, projectState.sourceSchema with
    | Some sourceDbPath, Some sourceSchema ->
      do! reportProgress $"Reading source database schema: {sourceDbPath}"
      return Some sourceSchema
    | _ -> return None
  }

let prepareNewDb (reportProgress: ProgReport) (project: MigProject) : Task<Result<string, MigError>> =
  taskResult {
    let! (projectState: ResolvedMigProject) = resolveProjectState project

    if File.Exists projectState.targetDbPath then
      return! Error(MigError.Regular $"Target database already exists: {Path.GetFullPath projectState.targetDbPath}")
    else
      do! reportProgress $"Creating target database: {projectState.targetDbPath}"
      let! (initResult: InitResult) = runInitWithSchema project.targetSchema projectState.targetDbPath
      return initResult.newDbPath
  }
