module internal MigLib.Commands.Resolution.Types

open MigLib.Commands.Types
open MigLib.CompiledSchema

type ResolvedProject =
  { migProject: MigProject
    runtimeProjectPath: string
    runtimeProjectDirectory: string
    runtimeProjectName: string
    schemaProjectPath: string
    schemaDirectory: string }

type ResolvedAssembly =
  { project: ResolvedProject
    assemblyName: string
    assemblyPath: string }

type ResolvedGeneratedSchema =
  { assembly: ResolvedAssembly
    moduleName: string
    generatedModule: GeneratedSchemaModule }

type ResolvedDatabasePaths =
  { targetDbPath: string
    sourceDbPath: string option
    archiveDirectory: string }
