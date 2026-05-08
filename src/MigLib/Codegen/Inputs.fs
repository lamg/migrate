module internal MigLib.Codegen.Inputs

open System
open System.IO

open MigLib.Types
open MigLib.Resolution.Assemblies
open MigLib.Resolution.Projects
open MigLib.Resolution.Types
open MigLib.TaskResult

type CodegenInputs =
  { project: ResolvedProjectLayout
    domainModelingAssembly: ResolvedAssembly
    schemaSourcePath: string
    outputPath: string }

let private regularError message = Error(MigError.Regular message)

let private resolveSchemaSourcePath (project: ResolvedProjectLayout) =
  let schemaSourcePath = Path.Combine(project.domainModelingDirectory, "MigSchema.fs")
  let fullSchemaSourcePath = Path.GetFullPath schemaSourcePath

  if File.Exists fullSchemaSourcePath then
    Ok fullSchemaSourcePath
  else
    regularError $"Schema source file was not found: {fullSchemaSourcePath}"

let resolveInputs (projectDir: string) : Result<CodegenInputs, MigError> =
  result {
    let! resolvedProject = discoverProjectLayout projectDir
    let! domainModelingAssembly = resolveDomainModelingAssembly resolvedProject
    let! schemaSourcePath = resolveSchemaSourcePath resolvedProject

    return
      { project = resolvedProject
        domainModelingAssembly = domainModelingAssembly
        schemaSourcePath = schemaSourcePath
        outputPath = Path.Combine(resolvedProject.domainModelingDirectory, "Db.fs") }
  }
