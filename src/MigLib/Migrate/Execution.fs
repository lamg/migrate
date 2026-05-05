module internal MigLib.Migrate.Execution

open System
open System.Threading.Tasks

open MigLib.Migrate.Archive
open MigLib.Migrate.DataCopy
open MigLib.Migrate.Discovery
open MigLib.Migrate.Planning
open MigLib.Types
open MigLib.Db.Transactions
open MigLib.TaskResult

let private formatUnsupportedDifferences (differences: string list) =
  let details =
    differences
    |> List.map (fun difference -> $"- {difference}")
    |> String.concat Environment.NewLine

  $"Migration plan has unsupported differences:{Environment.NewLine}{details}"

let migrate (reportProgress: ProgReport) (project: ResolvedProject) : Task<Result<MigrateResult, MigError>> =
  taskResult {
    let! migrationPlan = buildPlan reportProgress project

    if not migrationPlan.result.canMigrate then
      return! Error(MigError.Regular(formatUnsupportedDifferences migrationPlan.result.unsupportedDifferences))
    else
      let! newDbPath = prepareNewDb reportProgress project

      let! (copyResult: CopyResult) =
        match migrationPlan.sourceSchema, migrationPlan.result.sourceDbPath with
        | Some sourceSchema, Some sourceDbPath ->
          copyData reportProgress sourceDbPath newDbPath sourceSchema migrationPlan.targetSchema
        | _ -> Task.FromResult(Ok { copiedTables = 0; copiedRows = 0L })

      let! archivedOldDbPath =
        match migrationPlan.result.sourceDbPath with
        | Some sourceDbPath ->
          taskResult {
            let! (archivedPath: string) = markReadonlyAndArchiveOldDb reportProgress sourceDbPath
            return Some archivedPath
          }
        | None -> Task.FromResult(Ok None)

      return
        { db = dbTxn newDbPath
          newDbPath = newDbPath
          archivedOldDbPath = archivedOldDbPath
          copiedTables = copyResult.copiedTables
          copiedRows = copyResult.copiedRows }
  }
