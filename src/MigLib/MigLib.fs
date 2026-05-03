[<AutoOpen>]
module MigLib.MigLib

open System.Threading.Tasks
open MigLib.Db
open MigLib.TaskResult

type MigProject = Types.MigProject
type MigError = Types.MigError
type CodegenResult = Types.CodegenResult
type InitResult = Types.InitResult
type PlanResult = Types.PlanResult
type MigrateResult = Types.MigrateResult
type StatusResult = Types.StatusResult
type ResetResult = Types.ResetResult
type ProgReport = Types.ProgReport

let codegen (project: MigProject) : Result<CodegenResult, MigError> = Codegen.Execution.codegen project

let discoverProject (directory: string) (instance: string option) (dbDir: string) : Result<MigProject, MigError> =
  let resolveDatabaseInstance (instance: string option) =
    match instance with
    | Some value when not (System.String.IsNullOrWhiteSpace value) -> value.Trim()
    | _ -> "main"

  result {
    let dbInstance = resolveDatabaseInstance instance
    let! resolvedProject = Resolution.Projects.discoverProject directory dbInstance dbDir
    let! runtimeAssembly = Resolution.Assemblies.resolveRuntimeAssembly resolvedProject
    let! generatedSchema = Resolution.GeneratedSchema.resolveGeneratedSchema runtimeAssembly

    return
      { dbInstance = dbInstance
        dbDir = dbDir
        targetSchema = generatedSchema.generatedModule.schema
        dbApp = generatedSchema.generatedModule.dbApp
        schemaIdentity = generatedSchema.generatedModule.schemaIdentity }
  }

let init (project: MigProject) : Task<Result<InitResult, MigError>> = Init.Execution.init project

let migrate (reportProgress: ProgReport) (project: MigProject) : Task<Result<MigrateResult, MigError>> =
  Migrate.Execution.migrate reportProgress project

let plan (project: MigProject) : Task<Result<PlanResult, MigError>> = Plan.Reporting.plan project

// reports if the current database needs a migration
let status (project: MigProject) : Task<Result<StatusResult, MigError>> = Status.Execution.status project

// eliminates the current database and brings the parent database from the archive directory
let reset (project: MigProject) : Task<Result<ResetResult, MigError>> = Reset.Execution.reset project
