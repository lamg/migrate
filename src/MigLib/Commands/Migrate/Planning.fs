module internal MigLib.Commands.Migrate.Planning

open System.Threading.Tasks

open MigLib.Commands.Types

type MigrationPlan =
  { sourceSchema: SqlFile option
    targetSchema: SqlFile
    result: PlanResult }

let buildPlan (reportProgress: ProgReport) (project: MigProject) : Task<Result<MigrationPlan, MigError>> =
  failwith "TODO buildMigrationPlan"
