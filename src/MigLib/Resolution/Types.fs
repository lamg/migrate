module internal MigLib.Resolution.Types

open MigLib.Schema.Types
open MigLib.Types

type ResolvedProjectLayout =
  { runtimeProjectPath: string
    runtimeProjectDirectory: string
    runtimeProjectName: string
    schemaProjectPath: string
    schemaDirectory: string }

type ResolvedAssembly =
  { project: ResolvedProjectLayout
    assemblyName: string
    assemblyPath: string }

type ResolvedGeneratedSchema =
  { assembly: ResolvedAssembly
    moduleName: string
    generatedModule: ResolvedGeneratedSchemaModule }
