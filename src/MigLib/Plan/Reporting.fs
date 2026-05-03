module internal MigLib.Plan.Reporting

open System.Threading.Tasks

open MigLib.Migrate.Planning
open MigLib.Types
open MigLib.TaskResult

let plan (project: MigProject) : Task<Result<PlanResult, MigError>> =
  let reportProgress _ = Task.FromResult()

  taskResult {
    let! migrationPlan = buildPlan reportProgress project
    return migrationPlan.result
  }
