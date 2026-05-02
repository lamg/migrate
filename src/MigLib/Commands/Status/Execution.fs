module internal MigLib.Commands.Status.Execution

open System.IO
open System.Threading.Tasks

open MigLib.Commands.Resolution.ProjectState
open MigLib.Commands.Types
open MigLib.Util

let status (project: MigProject) : Task<Result<StatusResult, MigError>> =
  taskResult {
    let! (projectState: ResolvedMigProject) = resolveProjectState project

    return
      { currentDbPath = projectState.currentDbPath
        archivedDbPaths = projectState.archivedDbPaths
        needsMigration = projectState.sourceDbPath.IsSome }
  }
