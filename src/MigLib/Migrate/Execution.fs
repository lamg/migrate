module internal MigLib.Migrate.Execution

open System
open System.Threading.Tasks
open System.IO
open MigLib.Types
open MigLib.TaskResult

open MigLib.Migrate.Archive
open MigLib.Migrate.DataCopy
open MigLib.Migrate.Discovery
open MigLib.Migrate.Planning
open MigLib.Init.SchemaInit
open MigLib.Sqlite

let private formatUnsupportedDifferences (differences: string list) =
  let details =
    differences
    |> List.map (fun difference -> $"- {difference}")
    |> String.concat Environment.NewLine

  $"Migration plan has unsupported differences:{Environment.NewLine}{details}"

let private applyMissingSeedsAfterCopy reportProgress newDbPath targetSchema : Task<Result<int64, MigError>> =
  task {
    do! reportProgress "Applying missing seed rows after data copy"
    use connection = openConnection newDbPath
    let! seedResult = seedDatabaseIgnoringConflicts connection targetSchema

    match seedResult with
    | Error error -> return Error error
    | Ok seededRows ->
      do! reportProgress $"Applied {seededRows} seed row(s) after data copy."
      return Ok seededRows
  }

let migrate (reportProgress: ProgReport) (project: ResolvedProject) : Task<Result<MigrateResult, MigError>> =
  taskResult {
    let! migrationPlan = buildPlan reportProgress project

    match migrationPlan with
    | {
        result = {
                   supportedDifferences = []
                   unsupportedDifferences = []
                   targetDbPath = target
                 }
      } when File.Exists target ->
      do! reportProgress "No migration needed"

      return
        {
          db = dbTxn migrationPlan.result.targetDbPath
          newDbPath = migrationPlan.result.targetDbPath
          archivedOldDbPath = None
          copiedTables = 0
          copiedRows = 0
        }
    | { result = { canMigrate = false } } ->
      return! Error(MigError.Regular(formatUnsupportedDifferences migrationPlan.result.unsupportedDifferences))
    | _ ->
      let! newDbPath = prepareNewDb reportProgress project

      let! (copyResult: CopyResult) =
        match migrationPlan.sourceSchema, migrationPlan.result.sourceDbPath with
        | Some sourceSchema, Some sourceDbPath ->
          copyData reportProgress sourceDbPath newDbPath sourceSchema migrationPlan.targetSchema
        | _ -> Task.FromResult(Ok { copiedTables = 0; copiedRows = 0L })

      let! (_: int64) =
        match migrationPlan.result.sourceDbPath with
        | Some _ -> applyMissingSeedsAfterCopy reportProgress newDbPath migrationPlan.targetSchema
        | None -> Task.FromResult(Ok 0L)

      let! archivedOldDbPath =
        match migrationPlan.result.sourceDbPath with
        | Some sourceDbPath ->
          taskResult {
            let! (archivedPath: string) = markReadonlyAndArchiveOldDb reportProgress sourceDbPath
            return Some archivedPath
          }
        | None -> Task.FromResult(Ok None)

      return
        {
          db = dbTxn newDbPath
          newDbPath = newDbPath
          archivedOldDbPath = archivedOldDbPath
          copiedTables = copyResult.copiedTables
          copiedRows = copyResult.copiedRows
        }
  }
