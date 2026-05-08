module internal MigLib.Resolution.Types

open MigLib.Schema.Types
open MigLib.Types

type ResolvedProjectLayout =
  { runtimeProjectPath: string
    runtimeProjectDirectory: string
    runtimeProjectName: string
    domainModelingProjectPath: string
    domainModelingDirectory: string }

type ResolvedAssembly =
  { project: ResolvedProjectLayout
    assemblyName: string
    assemblyPath: string }

type ResolvedGeneratedSchema =
  { assembly: ResolvedAssembly
    moduleName: string
    generatedModule: ResolvedGeneratedSchemaModule }
