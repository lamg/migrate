[<AutoOpen>]
module MigLib.MigLib

open System.Threading.Tasks
open MigLib.Db
open MigLib.TaskResult

type MigError = Types.MigError
type CodegenResult = Types.CodegenResult
type InitResult = Types.InitResult
type PlanResult = Types.PlanResult
type MigrateResult = Types.MigrateResult
type StatusResult = Types.StatusResult
type ResetResult = Types.ResetResult
type ProgReport = Types.ProgReport
type ResolvedProject = Types.ResolvedProject

let codegen (projectDir: string) : Result<CodegenResult, MigError> = Codegen.Execution.codegen projectDir

let discoverProject
  (projDir: string)
  (instance: string option)
  (dbDir: string)
  : Task<Result<ResolvedProject, MigError>> =
  MigLib.Resolution.Projects.discoverProject projDir instance dbDir

let resolveProjectFromGeneratedSchema
  (dbDir: string)
  (instance: string option)
  (generatedSchema: Types.ResolvedGeneratedSchemaModule)
  : Task<Result<ResolvedProject, MigError>> =
  MigLib.Resolution.Projects.resolveProjectFromGeneratedSchema dbDir instance generatedSchema

let init (project: ResolvedProject) : Task<Result<InitResult, MigError>> = Init.Execution.init project

let migrate (reportProgress: ProgReport) (project: ResolvedProject) : Task<Result<MigrateResult, MigError>> =
  Migrate.Execution.migrate reportProgress project

let plan (project: ResolvedProject) : Task<Result<PlanResult, MigError>> = Plan.Reporting.plan project

// reports if the current database needs a migration
let status (project: ResolvedProject) : Task<Result<StatusResult, MigError>> = Status.Execution.status project

// eliminates the current database and brings the parent database from the archive directory
let reset (project: ResolvedProject) : Task<Result<ResetResult, MigError>> = Reset.Execution.reset project
