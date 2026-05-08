module Test.Resolution.AssembliesTests

open System
open System.IO

open MigLib.Types
open MigLib.Resolution.Types
open MigLib.Resolution.Assemblies
open Xunit

let private createTempDir name =
  let path = Path.Combine(Path.GetTempPath(), $"{name}_{Guid.NewGuid()}")

  Directory.CreateDirectory path |> ignore
  path

let private writeFile (path: string) (text: string) =
  let directory = Path.GetDirectoryName path

  if not (String.IsNullOrWhiteSpace directory) then
    Directory.CreateDirectory directory |> ignore

  File.WriteAllText(path, text)

let private makeProject tempDir projectName =
  let projectPath = Path.Combine(tempDir, $"{projectName}.fsproj")
  let domainModelingDirectory = Path.Combine(tempDir, "DomainModeling")

  { runtimeProjectPath = projectPath
    runtimeProjectDirectory = tempDir
    runtimeProjectName = projectName
    domainModelingProjectPath = Path.Combine(domainModelingDirectory, "DomainModeling.fsproj")
    domainModelingDirectory = domainModelingDirectory }

let private runtimeDllPath tempDir targetFramework assemblyName =
  Path.Combine(tempDir, "bin", "Debug", targetFramework, $"{assemblyName}.dll")

let private domainModelingDllPath tempDir targetFramework assemblyName =
  Path.Combine(tempDir, "DomainModeling", "bin", "Debug", targetFramework, $"{assemblyName}.dll")

let private assertRegularErrorContains expectedText result =
  match result with
  | Error(MigError.Regular message) -> Assert.Contains(expectedText, message)
  | Error error -> failwith $"Expected MigError.Regular, got: {error}"
  | Ok value -> failwith $"Expected error, got: {value}"

[<Fact>]
let ``resolveRuntimeAssembly uses project file name when AssemblyName is absent`` () =
  let tempDir = createTempDir "mig_resolve_runtime_assembly_default_name"

  try
    let project = makeProject tempDir "Runtime"
    writeFile project.runtimeProjectPath "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

    let expectedAssemblyPath = runtimeDllPath tempDir "net10.0" "Runtime"
    writeFile expectedAssemblyPath ""

    match resolveRuntimeAssembly project with
    | Ok assembly ->
      Assert.Equal(project, assembly.project)
      Assert.Equal("Runtime", assembly.assemblyName)
      Assert.Equal(Path.GetFullPath expectedAssemblyPath, assembly.assemblyPath)
    | Error error -> failwith $"Expected runtime assembly to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveRuntimeAssembly uses AssemblyName when present`` () =
  let tempDir = createTempDir "mig_resolve_runtime_assembly_custom_name"

  try
    let project = makeProject tempDir "Runtime"

    writeFile
      project.runtimeProjectPath
      "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>Custom.Runtime</AssemblyName></PropertyGroup></Project>"

    let expectedAssemblyPath = runtimeDllPath tempDir "net10.0" "Custom.Runtime"
    writeFile expectedAssemblyPath ""

    match resolveRuntimeAssembly project with
    | Ok assembly ->
      Assert.Equal("Custom.Runtime", assembly.assemblyName)
      Assert.Equal(Path.GetFullPath expectedAssemblyPath, assembly.assemblyPath)
    | Error error -> failwith $"Expected runtime assembly to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveRuntimeAssembly uses TargetFramework when present`` () =
  let tempDir = createTempDir "mig_resolve_runtime_assembly_target_framework"

  try
    let project = makeProject tempDir "Runtime"

    writeFile
      project.runtimeProjectPath
      "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>"

    let expectedAssemblyPath = runtimeDllPath tempDir "net9.0" "Runtime"
    writeFile expectedAssemblyPath ""

    match resolveRuntimeAssembly project with
    | Ok assembly ->
      Assert.Equal("Runtime", assembly.assemblyName)
      Assert.Equal(Path.GetFullPath expectedAssemblyPath, assembly.assemblyPath)
    | Error error -> failwith $"Expected runtime assembly to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveRuntimeAssembly returns regular error when project file is missing`` () =
  let tempDir = createTempDir "mig_resolve_runtime_assembly_missing_project"

  try
    let project = makeProject tempDir "Runtime"

    resolveRuntimeAssembly project
    |> assertRegularErrorContains "runtime project file was not found"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveRuntimeAssembly returns regular error when build output is missing`` () =
  let tempDir = createTempDir "mig_resolve_runtime_assembly_missing_dll"

  try
    let project = makeProject tempDir "Runtime"
    writeFile project.runtimeProjectPath "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

    let result = resolveRuntimeAssembly project

    result |> assertRegularErrorContains "Could not resolve runtime assembly"
    result |> assertRegularErrorContains "Expected build output"
    result |> assertRegularErrorContains "Build the runtime project first"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveRuntimeAssembly trims project property values`` () =
  let tempDir = createTempDir "mig_resolve_runtime_assembly_trimmed_values"

  try
    let project = makeProject tempDir "Runtime"

    writeFile
      project.runtimeProjectPath
      "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>  Custom.Runtime  </AssemblyName><TargetFramework>  net9.0  </TargetFramework></PropertyGroup></Project>"

    let expectedAssemblyPath = runtimeDllPath tempDir "net9.0" "Custom.Runtime"
    writeFile expectedAssemblyPath ""

    match resolveRuntimeAssembly project with
    | Ok assembly ->
      Assert.Equal("Custom.Runtime", assembly.assemblyName)
      Assert.Equal(Path.GetFullPath expectedAssemblyPath, assembly.assemblyPath)
    | Error error -> failwith $"Expected runtime assembly to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveDomainModelingAssembly uses DomainModeling project file name when AssemblyName is absent`` () =
  let tempDir = createTempDir "mig_resolve_domain_modeling_assembly_default_name"

  try
    let project = makeProject tempDir "Runtime"
    writeFile project.domainModelingProjectPath "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

    let expectedAssemblyPath = domainModelingDllPath tempDir "net10.0" "DomainModeling"
    writeFile expectedAssemblyPath ""

    match resolveDomainModelingAssembly project with
    | Ok assembly ->
      Assert.Equal(project, assembly.project)
      Assert.Equal("DomainModeling", assembly.assemblyName)
      Assert.Equal(Path.GetFullPath expectedAssemblyPath, assembly.assemblyPath)
    | Error error -> failwith $"Expected DomainModeling assembly to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveDomainModelingAssembly uses AssemblyName when present`` () =
  let tempDir = createTempDir "mig_resolve_domain_modeling_assembly_custom_name"

  try
    let project = makeProject tempDir "Runtime"

    writeFile
      project.domainModelingProjectPath
      "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>Custom.DomainModeling</AssemblyName></PropertyGroup></Project>"

    let expectedAssemblyPath =
      domainModelingDllPath tempDir "net10.0" "Custom.DomainModeling"

    writeFile expectedAssemblyPath ""

    match resolveDomainModelingAssembly project with
    | Ok assembly ->
      Assert.Equal("Custom.DomainModeling", assembly.assemblyName)
      Assert.Equal(Path.GetFullPath expectedAssemblyPath, assembly.assemblyPath)
    | Error error -> failwith $"Expected DomainModeling assembly to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveDomainModelingAssembly uses TargetFramework when present`` () =
  let tempDir = createTempDir "mig_resolve_domain_modeling_assembly_target_framework"

  try
    let project = makeProject tempDir "Runtime"

    writeFile
      project.domainModelingProjectPath
      "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>"

    let expectedAssemblyPath = domainModelingDllPath tempDir "net9.0" "DomainModeling"
    writeFile expectedAssemblyPath ""

    match resolveDomainModelingAssembly project with
    | Ok assembly ->
      Assert.Equal("DomainModeling", assembly.assemblyName)
      Assert.Equal(Path.GetFullPath expectedAssemblyPath, assembly.assemblyPath)
    | Error error -> failwith $"Expected DomainModeling assembly to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveDomainModelingAssembly returns regular error when project file is missing`` () =
  let tempDir = createTempDir "mig_resolve_domain_modeling_assembly_missing_project"

  try
    let project = makeProject tempDir "Runtime"

    resolveDomainModelingAssembly project
    |> assertRegularErrorContains "DomainModeling project file was not found"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveDomainModelingAssembly returns regular error when build output is missing`` () =
  let tempDir = createTempDir "mig_resolve_domain_modeling_assembly_missing_dll"

  try
    let project = makeProject tempDir "Runtime"
    writeFile project.domainModelingProjectPath "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

    let result = resolveDomainModelingAssembly project

    result |> assertRegularErrorContains "Could not resolve DomainModeling assembly"
    result |> assertRegularErrorContains "Expected build output"
    result |> assertRegularErrorContains "Build the DomainModeling project first"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveDomainModelingAssembly trims project property values`` () =
  let tempDir = createTempDir "mig_resolve_domain_modeling_assembly_trimmed_values"

  try
    let project = makeProject tempDir "Runtime"

    writeFile
      project.domainModelingProjectPath
      "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>  Custom.DomainModeling  </AssemblyName><TargetFramework>  net9.0  </TargetFramework></PropertyGroup></Project>"

    let expectedAssemblyPath =
      domainModelingDllPath tempDir "net9.0" "Custom.DomainModeling"

    writeFile expectedAssemblyPath ""

    match resolveDomainModelingAssembly project with
    | Ok assembly ->
      Assert.Equal("Custom.DomainModeling", assembly.assemblyName)
      Assert.Equal(Path.GetFullPath expectedAssemblyPath, assembly.assemblyPath)
    | Error error -> failwith $"Expected DomainModeling assembly to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)
