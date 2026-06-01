module internal MigLib.Codegen.Inputs

open System
open System.IO

open MigLib.Types
open MigLib.Resolution.Assemblies
open MigLib.Resolution.Projects
open MigLib.Resolution.Types
open MigLib.TaskResult

type CodegenInputs =
  {
    project: ResolvedProjectLayout
    schemaAssembly: ResolvedAssembly
    outputPath: string
  }

let resolveInputs (projectDir: string) : Result<CodegenInputs, MigError> =
  result {
    let! resolvedProject = discoverProjectLayout projectDir
    let! schemaAssembly = resolveSchemaAssembly resolvedProject

    return
      {
        project = resolvedProject
        schemaAssembly = schemaAssembly
        outputPath = Path.Combine(resolvedProject.schemaDirectory, "Db.fs")
      }
  }
