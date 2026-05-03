module internal MigLib.Commands.Resolution.ProjectState

open System.IO
open System.Threading.Tasks

open MigLib.Commands.Migrate.SchemaIntrospection
open MigLib.Commands.Types
open MigLib.Commands.Resolution.DatabasePaths
open MigLib.TaskResult
open MigLib.Sqlite

let private listArchivedDatabases archiveDirectory =
  if Directory.Exists archiveDirectory then
    Directory.GetFiles(archiveDirectory, "*.sqlite")
    |> Array.map Path.GetFullPath
    |> Array.sort
    |> Array.toList
  else
    []

let private loadSourceSchema (sourceDbPath: string) =
  taskResult {
    use connection = openConnection sourceDbPath
    let! (sourceSchema: SqlFile) = loadSchemaFromDatabase connection
    return Some sourceSchema
  }

let resolveProjectState (project: MigProject) : Task<Result<ResolvedMigProject, MigError>> =
  taskResult {
    let! paths = resolveDatabasePaths project

    let sourceSchemaResult =
      match paths.sourceDbPath with
      | Some sourceDbPath -> loadSourceSchema sourceDbPath
      | None -> Task.FromResult(Ok None)

    let! sourceSchema = sourceSchemaResult

    let targetDbPath = Path.GetFullPath paths.targetDbPath
    let targetExists = File.Exists targetDbPath

    let currentDbPath =
      if targetExists then
        Some targetDbPath
      else
        paths.sourceDbPath

    return
      { project = project
        targetDbPath = targetDbPath
        sourceDbPath = paths.sourceDbPath
        archiveDirectory = paths.archiveDirectory
        archivedDbPaths = listArchivedDatabases paths.archiveDirectory
        currentDbPath = currentDbPath
        sourceSchema = sourceSchema }
  }
