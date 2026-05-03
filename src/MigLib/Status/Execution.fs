module internal MigLib.Status.Execution

open System.IO
open System.Threading.Tasks

open MigLib.Resolution.ProjectState
open MigLib.Types
open MigLib.TaskResult

let status (project: MigProject) : Task<Result<StatusResult, MigError>> =
  taskResult {
    let! (projectState: ResolvedMigProject) = resolveProjectState project

    return
      { currentDbPath = projectState.currentDbPath
        archivedDbPaths = projectState.archivedDbPaths
        needsMigration = projectState.sourceDbPath.IsSome }
  }
