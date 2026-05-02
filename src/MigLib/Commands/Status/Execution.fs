module internal MigLib.Commands.Status.Execution

open System.IO
open System.Threading.Tasks

open MigLib.Commands.Migrate.Discovery
open MigLib.Commands.Types
open MigLib.Util

let private listArchivedDatabases archiveDirectory =
  if Directory.Exists archiveDirectory then
    Directory.GetFiles(archiveDirectory, "*.sqlite")
    |> Array.map Path.GetFullPath
    |> Array.sort
    |> Array.toList
  else
    []

let status (project: MigProject) : Task<Result<StatusResult, MigError>> =
  taskResult {
    let! _, paths = resolveMigrationInputs project

    let targetDbPath = Path.GetFullPath paths.targetDbPath
    let targetExists = File.Exists targetDbPath

    let currentDbPath =
      if targetExists then
        Some targetDbPath
      else
        paths.sourceDbPath

    return
      { currentDbPath = currentDbPath
        archivedDbPaths = listArchivedDatabases paths.archiveDirectory
        needsMigration = paths.sourceDbPath.IsSome }
  }
