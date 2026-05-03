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
  let schemaDirectory = Path.Combine(tempDir, "MigSchema")

  { dbInstance = "main"
    dbDir = tempDir
    runtimeProjectPath = projectPath
    runtimeProjectDirectory = tempDir
    runtimeProjectName = projectName
    schemaProjectPath = Path.Combine(schemaDirectory, "MigSchema.fsproj")
    schemaDirectory = schemaDirectory }

let private runtimeDllPath tempDir targetFramework assemblyName =
  Path.Combine(tempDir, "bin", "Debug", targetFramework, $"{assemblyName}.dll")

let private schemaDllPath tempDir targetFramework assemblyName =
  Path.Combine(tempDir, "MigSchema", "bin", "Debug", targetFramework, $"{assemblyName}.dll")

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
let ``resolveSchemaAssembly uses MigSchema project file name when AssemblyName is absent`` () =
  let tempDir = createTempDir "mig_resolve_schema_assembly_default_name"

  try
    let project = makeProject tempDir "Runtime"
    writeFile project.schemaProjectPath "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

    let expectedAssemblyPath = schemaDllPath tempDir "net10.0" "MigSchema"
    writeFile expectedAssemblyPath ""

    match resolveSchemaAssembly project with
    | Ok assembly ->
      Assert.Equal(project, assembly.project)
      Assert.Equal("MigSchema", assembly.assemblyName)
      Assert.Equal(Path.GetFullPath expectedAssemblyPath, assembly.assemblyPath)
    | Error error -> failwith $"Expected schema assembly to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveSchemaAssembly uses AssemblyName when present`` () =
  let tempDir = createTempDir "mig_resolve_schema_assembly_custom_name"

  try
    let project = makeProject tempDir "Runtime"

    writeFile
      project.schemaProjectPath
      "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>Custom.Schema</AssemblyName></PropertyGroup></Project>"

    let expectedAssemblyPath = schemaDllPath tempDir "net10.0" "Custom.Schema"
    writeFile expectedAssemblyPath ""

    match resolveSchemaAssembly project with
    | Ok assembly ->
      Assert.Equal("Custom.Schema", assembly.assemblyName)
      Assert.Equal(Path.GetFullPath expectedAssemblyPath, assembly.assemblyPath)
    | Error error -> failwith $"Expected schema assembly to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveSchemaAssembly uses TargetFramework when present`` () =
  let tempDir = createTempDir "mig_resolve_schema_assembly_target_framework"

  try
    let project = makeProject tempDir "Runtime"

    writeFile
      project.schemaProjectPath
      "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>"

    let expectedAssemblyPath = schemaDllPath tempDir "net9.0" "MigSchema"
    writeFile expectedAssemblyPath ""

    match resolveSchemaAssembly project with
    | Ok assembly ->
      Assert.Equal("MigSchema", assembly.assemblyName)
      Assert.Equal(Path.GetFullPath expectedAssemblyPath, assembly.assemblyPath)
    | Error error -> failwith $"Expected schema assembly to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveSchemaAssembly returns regular error when project file is missing`` () =
  let tempDir = createTempDir "mig_resolve_schema_assembly_missing_project"

  try
    let project = makeProject tempDir "Runtime"

    resolveSchemaAssembly project
    |> assertRegularErrorContains "schema project file was not found"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveSchemaAssembly returns regular error when build output is missing`` () =
  let tempDir = createTempDir "mig_resolve_schema_assembly_missing_dll"

  try
    let project = makeProject tempDir "Runtime"
    writeFile project.schemaProjectPath "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

    let result = resolveSchemaAssembly project

    result |> assertRegularErrorContains "Could not resolve schema assembly"
    result |> assertRegularErrorContains "Expected build output"
    result |> assertRegularErrorContains "Build the schema project first"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveSchemaAssembly trims project property values`` () =
  let tempDir = createTempDir "mig_resolve_schema_assembly_trimmed_values"

  try
    let project = makeProject tempDir "Runtime"

    writeFile
      project.schemaProjectPath
      "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>  Custom.Schema  </AssemblyName><TargetFramework>  net9.0  </TargetFramework></PropertyGroup></Project>"

    let expectedAssemblyPath = schemaDllPath tempDir "net9.0" "Custom.Schema"
    writeFile expectedAssemblyPath ""

    match resolveSchemaAssembly project with
    | Ok assembly ->
      Assert.Equal("Custom.Schema", assembly.assemblyName)
      Assert.Equal(Path.GetFullPath expectedAssemblyPath, assembly.assemblyPath)
    | Error error -> failwith $"Expected schema assembly to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)
