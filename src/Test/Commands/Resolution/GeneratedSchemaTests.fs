module Test.Commands.Resolution.GeneratedSchemaTests

open System
open System.IO

open MigLib.Commands.Types
open MigLib.Commands.Resolution.GeneratedSchema
open MigLib.Commands.Resolution.Types
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

let private makeAssembly tempDir projectXml =
  let runtimeProjectPath = Path.Combine(tempDir, "Runtime.fsproj")
  let schemaDirectory = Path.Combine(tempDir, "MigSchema")

  writeFile runtimeProjectPath projectXml

  { project =
      { migProject =
          { fsProject = runtimeProjectPath
            dbInstance = "main"
            dbDir = tempDir }
        runtimeProjectPath = runtimeProjectPath
        runtimeProjectDirectory = tempDir
        runtimeProjectName = "Runtime"
        schemaProjectPath = Path.Combine(schemaDirectory, "MigSchema.fsproj")
        schemaDirectory = schemaDirectory }
    assemblyName = "Test"
    assemblyPath = typeof<TestGenerated.Db.Marker>.Assembly.Location }

let private assertRegularErrorContains expectedText result =
  match result with
  | Error(MigError.Regular message) -> Assert.Contains(expectedText, message)
  | Error error -> failwith $"Expected MigError.Regular, got: {error}"
  | Ok value -> failwith $"Expected error, got: {value}"

[<Fact>]
let ``resolveSchemaModuleName uses runtime project RootNamespace`` () =
  let tempDir = createTempDir "mig_resolve_generated_schema_module"

  try
    let assembly =
      makeAssembly
        tempDir
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>TestGenerated</RootNamespace></PropertyGroup></Project>"

    match resolveSchemaModuleName assembly with
    | Ok moduleName -> Assert.Equal("TestGenerated.Db", moduleName)
    | Error error -> failwith $"Expected module name to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveSchemaModuleName fails when RootNamespace is absent`` () =
  let tempDir = createTempDir "mig_resolve_generated_schema_missing_root_namespace"

  try
    let assembly = makeAssembly tempDir "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

    resolveSchemaModuleName assembly
    |> assertRegularErrorContains "must define <RootNamespace>"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveGeneratedSchema loads Schema from conventional Db module`` () =
  let tempDir = createTempDir "mig_resolve_generated_schema"

  try
    let assembly =
      makeAssembly
        tempDir
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>TestGenerated</RootNamespace></PropertyGroup></Project>"

    match resolveGeneratedSchema assembly with
    | Ok resolved ->
      Assert.Equal(assembly, resolved.assembly)
      Assert.Equal("TestGenerated.Db", resolved.moduleName)
      Assert.Single resolved.generatedModule.schema.tables |> ignore
      Assert.Equal("generated_fixture", resolved.generatedModule.schema.tables.Head.name)
      Assert.Equal(TestGenerated.Db.SchemaHash, resolved.generatedModule.schemaHash)
      Assert.Equal(TestGenerated.Db.DbApp, resolved.generatedModule.dbApp)
      Assert.Equal(TestGenerated.Db.DefaultDbInstance, resolved.generatedModule.defaultDbInstance)
      Assert.Equal(TestGenerated.Db.SchemaIdentity, resolved.generatedModule.schemaIdentity)
    | Error error -> failwith $"Expected generated schema to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveGeneratedSchema fails when conventional Db module is absent`` () =
  let tempDir = createTempDir "mig_resolve_generated_schema_missing_module"

  try
    let assembly =
      makeAssembly
        tempDir
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>MissingGenerated</RootNamespace></PropertyGroup></Project>"

    resolveGeneratedSchema assembly
    |> assertRegularErrorContains "Compiled module 'MissingGenerated.Db' was not found"
  finally
    Directory.Delete(tempDir, true)
