module internal MigLib.Resolution.Assemblies

open System
open System.IO
open System.Xml.Linq

open MigLib.Types
open MigLib.Resolution.Types
open MigLib.TaskResult

let private tryLoadProjectDocument (projectKind: string) (projectPath: string) : Result<XDocument, MigError> =
  try
    if not (File.Exists projectPath) then
      Error(MigError.Regular $"{projectKind} project file was not found: {Path.GetFullPath projectPath}")
    else
      Ok(XDocument.Load projectPath)
  with ex ->
    Error(MigError.Regular $"Could not read {projectKind} project file '{Path.GetFullPath projectPath}': {ex.Message}")

let private tryReadProperty (name: string) (document: XDocument) =
  document.Descendants()
  |> Seq.tryFind (fun element -> String.Equals(element.Name.LocalName, name, StringComparison.Ordinal))
  |> Option.map _.Value
  |> Option.map _.Trim()
  |> Option.filter (String.IsNullOrWhiteSpace >> not)

let private resolveAssemblyName (fallbackAssemblyName: string) (document: XDocument) =
  document
  |> tryReadProperty "AssemblyName"
  |> Option.defaultValue fallbackAssemblyName

let private resolveTargetFramework (document: XDocument) =
  document |> tryReadProperty "TargetFramework" |> Option.defaultValue "net10.0"

let private buildAssemblyPath (projectDirectory: string) (targetFramework: string) (assemblyName: string) =
  Path.Combine(projectDirectory, "bin", "Debug", targetFramework, $"{assemblyName}.dll")

let resolveAssemblyPath (projectKind: string) (projectPath: string) =
  result {
    let! document = tryLoadProjectDocument projectKind projectPath
    let projectDirectory = Path.GetDirectoryName projectPath
    let fallbackName = Path.GetFileNameWithoutExtension projectPath
    let assemblyName = resolveAssemblyName fallbackName document
    let targetFramework = resolveTargetFramework document
    let assemblyPath = buildAssemblyPath projectDirectory targetFramework assemblyName
    let fullAssemblyPath = Path.GetFullPath assemblyPath
    return assemblyName, fullAssemblyPath
  }

let private requireAssemblyFile projectKind buildHint (assemblyPath: string) =
  if File.Exists assemblyPath then
    Ok assemblyPath
  else
    Error(
      MigError.Regular
        $"Could not resolve {projectKind} assembly. Expected build output: {assemblyPath}. Build the {buildHint} project first."
    )

let private resolveProjectAssembly projectKind buildHint projectPath project =
  result {
    let! assemblyName, assemblyPath = resolveAssemblyPath projectKind projectPath
    let! existingAssemblyPath = requireAssemblyFile projectKind buildHint assemblyPath

    return
      { project = project
        assemblyName = assemblyName
        assemblyPath = existingAssemblyPath }
  }

let resolveAssembly (projectPath: string) =
  result {
    let! _, assemblyPath = resolveAssemblyPath "project" projectPath
    return assemblyPath
  }

let resolveRuntimeAssembly (project: ResolvedProjectLayout) =
  resolveProjectAssembly "runtime" "runtime" project.runtimeProjectPath project

let resolveDomainModelingAssembly (project: ResolvedProjectLayout) =
  resolveProjectAssembly "DomainModeling" "DomainModeling" project.domainModelingProjectPath project
