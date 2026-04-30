module internal MigLib.Commands.Resolution.Assemblies

open System
open System.IO
open System.Xml.Linq

open MigLib.Commands.Types
open MigLib.Commands.Resolution.Types
open MigLib.Util

let private tryLoadProjectDocument (projectPath: string) : Result<XDocument, MigError> =
  try
    if String.IsNullOrWhiteSpace projectPath then
      Error(MigError.Regular "Runtime project path is empty.")
    elif not (File.Exists projectPath) then
      Error(MigError.Regular $"Runtime project file was not found: {Path.GetFullPath projectPath}")
    else
      Ok(XDocument.Load projectPath)
  with ex ->
    Error(MigError.Regular $"Could not read runtime project file '{Path.GetFullPath projectPath}': {ex.Message}")

let private tryReadProperty (name: string) (document: XDocument) =
  document.Descendants()
  |> Seq.tryFind (fun element -> String.Equals(element.Name.LocalName, name, StringComparison.Ordinal))
  |> Option.map _.Value
  |> Option.map _.Trim()
  |> Option.filter (String.IsNullOrWhiteSpace >> not)

let private resolveAssemblyName (project: ResolvedProject) (document: XDocument) =
  document
  |> tryReadProperty "AssemblyName"
  |> Option.defaultValue project.runtimeProjectName

let private resolveTargetFramework (document: XDocument) =
  document |> tryReadProperty "TargetFramework" |> Option.defaultValue "net10.0"

let private resolveAssemblyPath (project: ResolvedProject) (targetFramework: string) (assemblyName: string) =
  Path.Combine(project.runtimeProjectDirectory, "bin", "Debug", targetFramework, $"{assemblyName}.dll")

let resolveRuntimeAssembly (project: ResolvedProject) : Result<ResolvedAssembly, MigError> =
  result {
    let! document = tryLoadProjectDocument project.runtimeProjectPath
    let assemblyName = resolveAssemblyName project document
    let targetFramework = resolveTargetFramework document
    let assemblyPath = resolveAssemblyPath project targetFramework assemblyName
    let fullAssemblyPath = Path.GetFullPath assemblyPath

    if File.Exists fullAssemblyPath then
      return
        { project = project
          assemblyName = assemblyName
          assemblyPath = fullAssemblyPath }
    else
      return!
        Error(
          MigError.Regular
            $"Could not resolve runtime assembly. Expected build output at '{fullAssemblyPath}'. Build the runtime project first."
        )
  }

let resolveSchemaAssembly (project: ResolvedProject) : Result<ResolvedAssembly, MigError> =
  failwith "TODO resolveSchemaAssembly"
