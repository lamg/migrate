module internal MigLib.Commands.Resolution.Types

open MigLib.Commands.Schema.Types
open MigLib.Commands.Types

type ResolvedProject =
  { dbInstance: string
    dbDir: string
    runtimeProjectPath: string
    runtimeProjectDirectory: string
    runtimeProjectName: string
    schemaProjectPath: string
    schemaDirectory: string }

type ResolvedAssembly =
  { project: ResolvedProject
    assemblyName: string
    assemblyPath: string }

type ResolvedGeneratedSchemaModule =
  { schema: SqlFile
    schemaIdentity: SchemaIdentity
    schemaHash: string
    dbApp: string
    defaultDbInstance: string }

type ResolvedGeneratedSchema =
  { assembly: ResolvedAssembly
    moduleName: string
    generatedModule: ResolvedGeneratedSchemaModule }

type ResolvedDatabasePaths =
  { targetDbPath: string
    sourceDbPath: string option
    archiveDirectory: string }
