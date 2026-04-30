module internal MigLib.Commands.Codegen.Inputs

open MigLib.Commands.Types
open MigLib.Commands.Resolution.Types

type CodegenInputs =
  { project: ResolvedProject
    schemaAssembly: ResolvedAssembly
    generatedModuleName: string
    outputPath: string }

let resolveInputs (project: MigProject) : Result<CodegenInputs, MigError> = failwith "TODO resolveCodegenInputs"
