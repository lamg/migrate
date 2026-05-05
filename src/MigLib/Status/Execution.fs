module internal MigLib.Status.Execution

open System.IO
open System.Threading.Tasks

open MigLib.Types
open MigLib.TaskResult

let status (project: ResolvedProject) : Task<Result<StatusResult, MigError>> =
  taskResult {
    let currentDbPath =
      if File.Exists project.targetDbPath then
        Some project.targetDbPath
      else
        project.sourceDbPath

    let archivedDbPaths =
      if Directory.Exists project.archiveDir then
        Directory.GetFiles(project.archiveDir, "*.sqlite")
        |> Array.map Path.GetFullPath
        |> Array.sort
        |> Array.toList
      else
        []

    return
      { currentDbPath = currentDbPath
        archivedDbPaths = archivedDbPaths
        needsMigration = project.sourceDbPath.IsSome }
  }
