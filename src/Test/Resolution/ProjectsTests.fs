module Test.Resolution.ProjectsTests

open System
open System.IO

open MigLib.Types
open MigLib.Resolution.Projects
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

let private runtimeProjectPath tempDir name = Path.Combine(tempDir, $"{name}.fsproj")

let private schemaProjectPath tempDir =
  Path.Combine(tempDir, "MigSchema", "MigSchema.fsproj")

let private runtimeAssemblyPath tempDir =
  let assemblyName =
    Path.GetFileNameWithoutExtension(typeof<TestGenerated.Db.Marker>.Assembly.Location)

  Path.Combine(tempDir, "bin", "Debug", "net10.0", $"{assemblyName}.dll")

let private writeRuntimeProject tempDir name =
  let fixtureAssembly = typeof<TestGenerated.Db.Marker>.Assembly.Location
  let assemblyName = Path.GetFileNameWithoutExtension fixtureAssembly

  writeFile
    (runtimeProjectPath tempDir name)
    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>TestGenerated</RootNamespace><AssemblyName>{assemblyName}</AssemblyName></PropertyGroup></Project>"

  let targetAssemblyPath = runtimeAssemblyPath tempDir
  Directory.CreateDirectory(Path.GetDirectoryName targetAssemblyPath) |> ignore
  File.Copy(fixtureAssembly, targetAssemblyPath, true)

let private writeSchemaProject tempDir =
  writeFile
    (schemaProjectPath tempDir)
    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>TestGeneratedSchema</RootNamespace></PropertyGroup></Project>"

let private assertRegularErrorContains expectedText result =
  match result with
  | Error(MigError.Regular message) -> Assert.Contains(expectedText, message)
  | Error error -> failwith $"Expected MigError.Regular, got: {error}"
  | Ok value -> failwith $"Expected error, got: {value}"

[<Fact>]
let ``resolveProjectLayout resolves explicit runtime project and MigSchema project`` () =
  let tempDir = createTempDir "mig_resolve_project_layout_explicit"

  try
    writeRuntimeProject tempDir "Runtime"
    writeSchemaProject tempDir

    match resolveProjectLayout (runtimeProjectPath tempDir "Runtime") with
    | Ok resolved ->
      Assert.Equal(Path.GetFullPath(runtimeProjectPath tempDir "Runtime"), resolved.runtimeProjectPath)
      Assert.Equal(tempDir, resolved.runtimeProjectDirectory)
      Assert.Equal("Runtime", resolved.runtimeProjectName)
      Assert.Equal(schemaProjectPath tempDir, resolved.schemaProjectPath)
      Assert.Equal(Path.Combine(tempDir, "MigSchema"), resolved.schemaDirectory)
    | Error error -> failwith $"Expected project layout to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveProject returns runtime schema and database paths`` () =
  let tempDir = createTempDir "mig_resolve_project_explicit"

  try
    writeRuntimeProject tempDir "Runtime"
    writeSchemaProject tempDir

    match
      resolveProject (runtimeProjectPath tempDir "Runtime") "main" tempDir
      |> fun task -> task.Result
    with
    | Ok resolved ->
      Assert.Equal(TestGenerated.Db.GeneratedSchema.dbApp, resolved.targetSchema.dbApp)
      Assert.Equal(TestGenerated.Db.GeneratedSchema.schemaHash, resolved.targetSchema.schemaHash)
      Assert.Equal(Path.Combine(tempDir, "generated-fixture-main-0123456789abcdef.sqlite"), resolved.targetDbPath)
      Assert.Equal(None, resolved.sourceDbPath)
      Assert.Equal(None, resolved.sourceDbSchema)
      Assert.Equal(Path.Combine(tempDir, "archive"), resolved.archiveDir)
    | Error error -> failwith $"Expected project to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``discoverProject resolves a single runtime project in directory`` () =
  let tempDir = createTempDir "mig_discover_project_single"

  try
    writeRuntimeProject tempDir "Runtime"
    writeSchemaProject tempDir

    match discoverProject tempDir (Some "tenant") tempDir |> fun task -> task.Result with
    | Ok resolved ->
      Assert.Equal(Path.Combine(tempDir, "generated-fixture-tenant-0123456789abcdef.sqlite"), resolved.targetDbPath)
      Assert.Equal(Path.Combine(tempDir, "archive"), resolved.archiveDir)
    | Error error -> failwith $"Expected project to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveProject fails when runtime project is missing`` () =
  let tempDir = createTempDir "mig_resolve_project_missing_runtime"

  try
    resolveProject (runtimeProjectPath tempDir "Missing") "main" tempDir
    |> fun task -> task.Result
    |> assertRegularErrorContains "Runtime project file was not found"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveProject fails when MigSchema project is missing`` () =
  let tempDir = createTempDir "mig_resolve_project_missing_schema"

  try
    writeRuntimeProject tempDir "Runtime"

    resolveProject (runtimeProjectPath tempDir "Runtime") "main" tempDir
    |> fun task -> task.Result
    |> assertRegularErrorContains "Schema project file was not found"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``discoverProject fails when directory has no runtime project`` () =
  let tempDir = createTempDir "mig_discover_project_none"

  try
    discoverProject tempDir None tempDir
    |> fun task -> task.Result
    |> assertRegularErrorContains "No .fsproj file was found"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``discoverProject fails when directory has multiple runtime projects`` () =
  let tempDir = createTempDir "mig_discover_project_multiple"

  try
    writeRuntimeProject tempDir "First"
    writeRuntimeProject tempDir "Second"

    discoverProject tempDir None tempDir
    |> fun task -> task.Result
    |> assertRegularErrorContains "Found multiple .fsproj files"
  finally
    Directory.Delete(tempDir, true)
