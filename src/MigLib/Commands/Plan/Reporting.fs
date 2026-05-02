module internal MigLib.Commands.Plan.Reporting

open System.Threading.Tasks

open MigLib.Commands.Migrate.Planning
open MigLib.Commands.Types
open MigLib.Util

let plan (project: MigProject) : Task<Result<PlanResult, MigError>> =
  let reportProgress _ = Task.FromResult()

  taskResult {
    let! migrationPlan = buildPlan reportProgress project
    return migrationPlan.result
  }
