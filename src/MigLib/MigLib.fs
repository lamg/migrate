[<AutoOpen>]
module MigLib.MigLib

open System.Threading.Tasks

type MigProject = Commands.Types.MigProject
type MigError = Commands.Types.MigError
type CodegenResult = Commands.Types.CodegenResult
type InitResult = Commands.Types.InitResult
type PlanResult = Commands.Types.PlanResult
type MigrateResult = Commands.Types.MigrateResult
type StatusResult = Commands.Types.StatusResult
type ResetResult = Commands.Types.ResetResult
type ProgReport = Commands.Types.ProgReport

let codegen (project: MigProject) : Result<CodegenResult, MigError> =
  Commands.Codegen.Execution.codegen project

let init (project: MigProject) : Task<Result<InitResult, MigError>> = Commands.Init.Execution.init project

let migrate (reportProgress: ProgReport) (project: MigProject) : Task<Result<MigrateResult, MigError>> =
  Commands.Migrate.Execution.migrate reportProgress project

let plan (project: MigProject) : Task<Result<PlanResult, MigError>> = Commands.Plan.Reporting.plan project

// reports if the current database needs a migration
let status (project: MigProject) : Task<Result<StatusResult, MigError>> =
  Commands.Status.Execution.status project

// eliminates the current database and brings the parent database from the archive directory
let reset (project: MigProject) : Task<Result<ResetResult, MigError>> = Commands.Reset.Execution.reset project
