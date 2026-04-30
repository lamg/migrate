module internal MigLib.Commands.Resolution

let resolveProject = MigLib.Commands.Resolution.Projects.resolveProject
let discoverProject = MigLib.Commands.Resolution.Projects.discoverProject

let resolveRuntimeAssembly =
  MigLib.Commands.Resolution.Assemblies.resolveRuntimeAssembly

let resolveSchemaAssembly =
  MigLib.Commands.Resolution.Assemblies.resolveSchemaAssembly

let resolveGeneratedSchema =
  MigLib.Commands.Resolution.GeneratedSchema.resolveGeneratedSchema

let resolveSchemaModuleName =
  MigLib.Commands.Resolution.GeneratedSchema.resolveSchemaModuleName

let resolveDatabasePaths =
  MigLib.Commands.Resolution.DatabasePaths.resolveDatabasePaths

let resolveTargetDbPath =
  MigLib.Commands.Resolution.DatabasePaths.resolveTargetDbPath

let resolveSourceDbPath =
  MigLib.Commands.Resolution.DatabasePaths.resolveSourceDbPath
