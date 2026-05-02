module internal MigLib.Commands.Migrate.Archive

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite

open MigLib.Commands.Types
open MigLib.Util

let private archivePathFor oldDbPath =
  let oldDirectory = Path.GetDirectoryName(Path.GetFullPath oldDbPath)

  if String.IsNullOrWhiteSpace oldDirectory then
    Path.Combine("archive", Path.GetFileName oldDbPath)
  else
    Path.Combine(oldDirectory, "archive", Path.GetFileName oldDbPath)

let private markReadonly oldDbPath =
  task {
    use connection = Sqlite.openConnection oldDbPath
    use tx = connection.BeginTransaction()

    use createTableCommand =
      Sqlite.createCommand
        connection
        tx
        "CREATE TABLE IF NOT EXISTS _mig_readonly(id INTEGER PRIMARY KEY CHECK (id = 1), marked_utc TEXT NOT NULL);"

    let! _ = createTableCommand.ExecuteNonQueryAsync()

    use upsertCommand =
      Sqlite.createCommand
        connection
        tx
        "INSERT INTO _mig_readonly(id, marked_utc) VALUES (1, @markedUtc) ON CONFLICT(id) DO UPDATE SET marked_utc = excluded.marked_utc;"

    upsertCommand.Parameters.AddWithValue("@markedUtc", DateTimeOffset.UtcNow.ToString("O"))
    |> ignore

    let! _ = upsertCommand.ExecuteNonQueryAsync()

    tx.Commit()
  }

let markReadonlyAndArchiveOldDb (reportProgress: ProgReport) (oldDbPath: string) : Task<Result<string, MigError>> =
  taskResult {
    let resolvedOldDbPath = Path.GetFullPath oldDbPath

    if not (File.Exists resolvedOldDbPath) then
      return! Error(MigError.Regular $"Old database was not found: {resolvedOldDbPath}")
    else
      let archivePath = archivePathFor resolvedOldDbPath

      if File.Exists archivePath then
        return! Error(MigError.Regular $"Archived database already exists: {Path.GetFullPath archivePath}")
      else
        do! reportProgress $"Marking old database readonly: {resolvedOldDbPath}"
        do! markReadonly resolvedOldDbPath

        let archiveDirectory = Path.GetDirectoryName archivePath

        if not (String.IsNullOrWhiteSpace archiveDirectory) then
          Directory.CreateDirectory archiveDirectory |> ignore

        do! reportProgress $"Archiving old database: {resolvedOldDbPath} -> {archivePath}"
        File.Move(resolvedOldDbPath, archivePath)
        return Path.GetFullPath archivePath
  }
