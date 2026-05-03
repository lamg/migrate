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

let private writeRuntimeProject tempDir name =
  writeFile (runtimeProjectPath tempDir name) "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

let private writeSchemaProject tempDir =
  writeFile (schemaProjectPath tempDir) "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

let private assertRegularErrorContains expectedText result =
  match result with
  | Error(MigError.Regular message) -> Assert.Contains(expectedText, message)
  | Error error -> failwith $"Expected MigError.Regular, got: {error}"
  | Ok value -> failwith $"Expected error, got: {value}"

[<Fact>]
let ``resolveProject resolves explicit runtime project and MigSchema project`` () =
  let tempDir = createTempDir "mig_resolve_project_explicit"

  try
    writeRuntimeProject tempDir "Runtime"
    writeSchemaProject tempDir

    let project =
      { dbInstance = "main"
        dbDir = tempDir
        targetSchema = TestGenerated.Db.Schema
        dbApp = TestGenerated.Db.DbApp
        schemaIdentity = TestGenerated.Db.SchemaIdentity }

    match resolveProject (runtimeProjectPath tempDir "Runtime") project.dbInstance project.dbDir with
    | Ok resolved ->
      Assert.Equal(project.dbInstance, resolved.dbInstance)
      Assert.Equal(project.dbDir, resolved.dbDir)
      Assert.Equal(Path.GetFullPath(runtimeProjectPath tempDir "Runtime"), resolved.runtimeProjectPath)
      Assert.Equal(tempDir, resolved.runtimeProjectDirectory)
      Assert.Equal("Runtime", resolved.runtimeProjectName)
      Assert.Equal(schemaProjectPath tempDir, resolved.schemaProjectPath)
      Assert.Equal(Path.Combine(tempDir, "MigSchema"), resolved.schemaDirectory)
    | Error error -> failwith $"Expected project to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``discoverProject resolves a single runtime project in directory`` () =
  let tempDir = createTempDir "mig_discover_project_single"

  try
    writeRuntimeProject tempDir "Runtime"
    writeSchemaProject tempDir

    match discoverProject tempDir "tenant" tempDir with
    | Ok resolved ->
      Assert.Equal(Path.GetFullPath(runtimeProjectPath tempDir "Runtime"), resolved.runtimeProjectPath)
      Assert.Equal("tenant", resolved.dbInstance)
      Assert.Equal(tempDir, resolved.dbDir)
      Assert.Equal(schemaProjectPath tempDir, resolved.schemaProjectPath)
    | Error error -> failwith $"Expected project to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveProject fails when runtime project is missing`` () =
  let tempDir = createTempDir "mig_resolve_project_missing_runtime"

  try
    let project =
      { dbInstance = "main"
        dbDir = tempDir
        targetSchema = TestGenerated.Db.Schema
        dbApp = TestGenerated.Db.DbApp
        schemaIdentity = TestGenerated.Db.SchemaIdentity }

    resolveProject (runtimeProjectPath tempDir "Missing") project.dbInstance project.dbDir
    |> assertRegularErrorContains "Runtime project file was not found"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveProject fails when MigSchema project is missing`` () =
  let tempDir = createTempDir "mig_resolve_project_missing_schema"

  try
    writeRuntimeProject tempDir "Runtime"

    let project =
      { dbInstance = "main"
        dbDir = tempDir
        targetSchema = TestGenerated.Db.Schema
        dbApp = TestGenerated.Db.DbApp
        schemaIdentity = TestGenerated.Db.SchemaIdentity }

    resolveProject (runtimeProjectPath tempDir "Runtime") project.dbInstance project.dbDir
    |> assertRegularErrorContains "Schema project file was not found"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``discoverProject fails when directory has no runtime project`` () =
  let tempDir = createTempDir "mig_discover_project_none"

  try
    discoverProject tempDir "main" tempDir
    |> assertRegularErrorContains "No .fsproj file was found"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``discoverProject fails when directory has multiple runtime projects`` () =
  let tempDir = createTempDir "mig_discover_project_multiple"

  try
    writeRuntimeProject tempDir "First"
    writeRuntimeProject tempDir "Second"

    discoverProject tempDir "main" tempDir
    |> assertRegularErrorContains "Found multiple .fsproj files"
  finally
    Directory.Delete(tempDir, true)
