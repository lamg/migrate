module internal MigLib.Commands.Migrate.Discovery

open System.IO
open System.Threading.Tasks

open MigLib.Commands.Init.Execution
open MigLib.Commands.Types
open MigLib.Commands.Migrate.SchemaIntrospection
open MigLib.Commands.Resolution.Assemblies
open MigLib.Commands.Resolution.DatabasePaths
open MigLib.Commands.Resolution.GeneratedSchema
open MigLib.Commands.Resolution.Projects
open MigLib.Commands.Resolution.Types
open MigLib.Util

let resolveMigrationInputs (project: MigProject) : Result<ResolvedGeneratedSchema * ResolvedDatabasePaths, MigError> =
  result {
    let! resolvedProject = resolveProject project
    let! runtimeAssembly = resolveRuntimeAssembly resolvedProject
    let! generatedSchema = resolveGeneratedSchema runtimeAssembly
    let! databasePaths = resolveDatabasePaths generatedSchema
    return generatedSchema, databasePaths
  }

let findOldSchema (reportProgress: ProgReport) (project: MigProject) : Task<Result<SqlFile option, MigError>> =
  taskResult {
    let! _, paths = resolveMigrationInputs project

    match paths.sourceDbPath with
    | None -> return None
    | Some sourceDbPath ->
      do! reportProgress $"Reading source database schema: {sourceDbPath}"
      use connection = Sqlite.openConnection sourceDbPath
      let! (schema: SqlFile) = loadSchemaFromDatabase connection
      return Some schema
  }

let prepareNewDb (reportProgress: ProgReport) (project: MigProject) : Task<Result<string, MigError>> =
  taskResult {
    let! generatedSchema, paths = resolveMigrationInputs project

    if File.Exists paths.targetDbPath then
      return! Error(MigError.Regular $"Target database already exists: {Path.GetFullPath paths.targetDbPath}")
    else
      do! reportProgress $"Creating target database: {paths.targetDbPath}"
      let! (initResult: InitResult) = runInitWithSchema generatedSchema.generatedModule.schema paths.targetDbPath
      return initResult.newDbPath
  }
