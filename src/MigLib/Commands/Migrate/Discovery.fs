module internal MigLib.Commands.Migrate.Discovery

open System.Threading.Tasks

open MigLib.Commands.Types
open MigLib.Commands.Resolution.Assemblies
open MigLib.Commands.Resolution.DatabasePaths
open MigLib.Commands.Resolution.GeneratedSchema
open MigLib.Commands.Resolution.Projects
open MigLib.Commands.Resolution.Types
open MigLib.Util

let findOldSchema (reportProgress: ProgReport) (project: MigProject) : Task<Result<SqlFile option, MigError>> =
  failwith "TODO findOldSchema"

let prepareNewDb (reportProgress: ProgReport) (project: MigProject) : Task<Result<string, MigError>> =
  failwith "TODO prepareNewDb"

let resolveMigrationInputs (project: MigProject) : Result<ResolvedGeneratedSchema * ResolvedDatabasePaths, MigError> =
  result {
    let! resolvedProject = resolveProject project
    let! runtimeAssembly = resolveRuntimeAssembly resolvedProject
    let! generatedSchema = resolveGeneratedSchema runtimeAssembly
    let! databasePaths = resolveDatabasePaths generatedSchema
    return generatedSchema, databasePaths
  }
