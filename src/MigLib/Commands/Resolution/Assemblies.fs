module internal MigLib.Commands.Resolution.Assemblies

open System
open System.IO
open System.Xml.Linq

open MigLib.Commands.Types
open MigLib.Commands.Resolution.Types
open MigLib.Util

let private tryLoadProjectDocument (projectKind: string) (projectPath: string) : Result<XDocument, MigError> =
  try
    if String.IsNullOrWhiteSpace projectPath then
      Error(MigError.Regular $"{projectKind} project path is empty.")
    elif not (File.Exists projectPath) then
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

let private resolveAssemblyPath (projectDirectory: string) (targetFramework: string) (assemblyName: string) =
  Path.Combine(projectDirectory, "bin", "Debug", targetFramework, $"{assemblyName}.dll")

let private resolveAssembly
  (projectKind: string)
  (projectPath: string)
  (projectDirectory: string)
  (fallbackAssemblyName: string)
  (project: ResolvedProject)
  : Result<ResolvedAssembly, MigError> =
  result {
    let! document = tryLoadProjectDocument projectKind projectPath
    let assemblyName = resolveAssemblyName fallbackAssemblyName document
    let targetFramework = resolveTargetFramework document
    let assemblyPath = resolveAssemblyPath projectDirectory targetFramework assemblyName
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
            $"Could not resolve {projectKind} assembly. Expected build output at '{fullAssemblyPath}'. Build the {projectKind} project first."
        )
  }

let resolveRuntimeAssembly (project: ResolvedProject) : Result<ResolvedAssembly, MigError> =
  resolveAssembly
    "runtime"
    project.runtimeProjectPath
    project.runtimeProjectDirectory
    project.runtimeProjectName
    project

let resolveSchemaAssembly (project: ResolvedProject) : Result<ResolvedAssembly, MigError> =
  // A conventional runtime project P stores its schema project at
  // P/MigSchema/MigSchema.fsproj, with schema source in P/MigSchema/MigSchema.fs.
  let schemaProjectName = Path.GetFileNameWithoutExtension project.schemaProjectPath

  resolveAssembly "schema" project.schemaProjectPath project.schemaDirectory schemaProjectName project
