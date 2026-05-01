module internal MigLib.Commands.Codegen.Inputs

open System
open System.IO
open System.Xml.Linq

open MigLib.Commands.Types
open MigLib.Commands.Resolution.Assemblies
open MigLib.Commands.Resolution.Projects
open MigLib.Commands.Resolution.Types
open MigLib.Util

type CodegenInputs =
  { project: ResolvedProject
    schemaAssembly: ResolvedAssembly
    schemaModuleName: string
    generatedModuleName: string
    schemaSourcePath: string
    dbApp: string
    outputPath: string }

let private regularError message = Error(MigError.Regular message)

let private tryLoadProjectDocument (projectKind: string) (projectPath: string) : Result<XDocument, MigError> =
  try
    if String.IsNullOrWhiteSpace projectPath then
      regularError $"{projectKind} project path is empty."
    elif not (File.Exists projectPath) then
      regularError $"{projectKind} project file was not found: {Path.GetFullPath projectPath}"
    else
      Ok(XDocument.Load projectPath)
  with ex ->
    regularError $"Could not read {projectKind} project file '{Path.GetFullPath projectPath}': {ex.Message}"

let private tryReadProperty (name: string) (document: XDocument) =
  document.Descendants()
  |> Seq.tryFind (fun element -> String.Equals(element.Name.LocalName, name, StringComparison.Ordinal))
  |> Option.map _.Value
  |> Option.map _.Trim()
  |> Option.filter (String.IsNullOrWhiteSpace >> not)

let private resolveRequiredRootNamespace (projectKind: string) (projectPath: string) =
  result {
    let! document = tryLoadProjectDocument projectKind projectPath

    return!
      document
      |> tryReadProperty "RootNamespace"
      |> ResultEx.requireSomeWith (fun () ->
        MigError.Regular $"{projectKind} project '{Path.GetFullPath projectPath}' must define <RootNamespace>.")
  }

let private resolveSchemaSourcePath (project: ResolvedProject) =
  let schemaSourcePath = Path.Combine(project.schemaDirectory, "MigSchema.fs")
  let fullSchemaSourcePath = Path.GetFullPath schemaSourcePath

  if File.Exists fullSchemaSourcePath then
    Ok fullSchemaSourcePath
  else
    regularError $"Schema source file was not found: {fullSchemaSourcePath}"

let resolveInputs (project: MigProject) : Result<CodegenInputs, MigError> =
  result {
    let! resolvedProject = resolveProject project
    let! schemaAssembly = resolveSchemaAssembly resolvedProject
    let! runtimeRootNamespace = resolveRequiredRootNamespace "runtime" resolvedProject.runtimeProjectPath
    let! schemaRootNamespace = resolveRequiredRootNamespace "schema" resolvedProject.schemaProjectPath
    let! schemaSourcePath = resolveSchemaSourcePath resolvedProject

    return
      { project = resolvedProject
        schemaAssembly = schemaAssembly
        schemaModuleName = $"{schemaRootNamespace}.MigSchema"
        generatedModuleName = $"{runtimeRootNamespace}.Db"
        schemaSourcePath = schemaSourcePath
        dbApp = runtimeRootNamespace
        outputPath = Path.Combine(resolvedProject.runtimeProjectDirectory, "Db.fs") }
  }
