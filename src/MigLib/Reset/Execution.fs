module internal MigLib.Reset.Execution

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite

open MigLib.Resolution.ProjectState
open MigLib.Types
open MigLib.TaskResult
open MigLib.Sqlite

let private latestArchivedDatabase (archivedDbPaths: string list) =
  archivedDbPaths
  |> List.sortByDescending File.GetLastWriteTimeUtc
  |> List.tryHead

let private restoreDestination (dbDirectory: string) (archivedDbPath: string) =
  Path.Combine(dbDirectory, Path.GetFileName archivedDbPath)

let private removeReadonlyMarker dbPath =
  task {
    use connection = openConnection dbPath
    use tx = connection.BeginTransaction()

    use command = createCommand connection tx "DROP TABLE IF EXISTS _mig_readonly;"

    let! _ = command.ExecuteNonQueryAsync()
    tx.Commit()
  }

let reset (project: MigProject) : Task<Result<ResetResult, MigError>> =
  taskResult {
    try
      let! (projectState: ResolvedMigProject) = resolveProjectState project
      let targetDbPath = Path.GetFullPath projectState.targetDbPath
      let dbDirectory = Path.GetDirectoryName targetDbPath

      let archivedDbPath = latestArchivedDatabase projectState.archivedDbPaths

      let restoredDbPath =
        archivedDbPath
        |> Option.map (restoreDestination dbDirectory >> Path.GetFullPath)

      match restoredDbPath with
      | Some destination when File.Exists destination ->
        return!
          Error(MigError.Regular $"Cannot restore archived database because destination already exists: {destination}")
      | _ ->
        let removedCurrentDbPath =
          if File.Exists targetDbPath then
            File.Delete targetDbPath
            Some targetDbPath
          else
            None

        match archivedDbPath, restoredDbPath with
        | Some archivePath, Some destination ->
          File.Move(archivePath, destination)
          do! removeReadonlyMarker destination

          return
            { restoredDbPath = Some destination
              removedCurrentDbPath = removedCurrentDbPath }
        | _ ->
          return
            { restoredDbPath = None
              removedCurrentDbPath = removedCurrentDbPath }
    with
    | :? SqliteException as ex -> return! Error(MigError.Sqlite ex)
    | ex -> return! Error(MigError.Other ex)
  }
