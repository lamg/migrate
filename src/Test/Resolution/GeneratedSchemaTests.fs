module Test.Resolution.GeneratedSchemaTests

open System
open System.IO

open MigLib.Types
open MigLib.Resolution.GeneratedSchema
open MigLib.Resolution.Types
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

let private makeAssembly tempDir assemblyPath =
  let runtimeProjectPath = Path.Combine(tempDir, "Runtime.fsproj")
  let domainModelingDirectory = Path.Combine(tempDir, "DomainModeling")

  writeFile runtimeProjectPath "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

  { project =
      { runtimeProjectPath = runtimeProjectPath
        runtimeProjectDirectory = tempDir
        runtimeProjectName = "Runtime"
        domainModelingProjectPath = Path.Combine(domainModelingDirectory, "DomainModeling.fsproj")
        domainModelingDirectory = domainModelingDirectory }
    assemblyName = "Test"
    assemblyPath = assemblyPath }

let private assertRegularErrorContains expectedText result =
  match result with
  | Error(MigError.Regular message) -> Assert.Contains(expectedText, message)
  | Error error -> failwith $"Expected MigError.Regular, got: {error}"
  | Ok value -> failwith $"Expected error, got: {value}"

[<Fact>]
let ``resolveSchemaModuleName finds compiled module containing GeneratedSchema`` () =
  let tempDir = createTempDir "mig_resolve_generated_schema_module"

  try
    let assembly =
      makeAssembly tempDir typeof<TestGenerated.Db.Marker>.Assembly.Location

    match resolveSchemaModuleName assembly with
    | Ok moduleName -> Assert.Equal("TestGenerated.Db", moduleName)
    | Error error -> failwith $"Expected module name to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveGeneratedSchema loads Schema from conventional Db module`` () =
  let tempDir = createTempDir "mig_resolve_generated_schema"

  try
    let assembly =
      makeAssembly tempDir typeof<TestGenerated.Db.Marker>.Assembly.Location

    match resolveGeneratedSchema assembly with
    | Ok resolved ->
      Assert.Equal(assembly, resolved.assembly)
      Assert.Equal("TestGenerated.Db", resolved.moduleName)
      Assert.Single resolved.generatedModule.schema.tables |> ignore
      Assert.Equal("generated_fixture", resolved.generatedModule.schema.tables.Head.name)
      Assert.Equal(TestGenerated.Db.GeneratedSchema.schemaHash, resolved.generatedModule.schemaHash)
      Assert.Equal(TestGenerated.Db.GeneratedSchema.dbApp, resolved.generatedModule.dbApp)
      Assert.Equal(TestGenerated.Db.GeneratedSchema.defaultDbInstance, resolved.generatedModule.defaultDbInstance)
    | Error error -> failwith $"Expected generated schema to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveGeneratedSchema fails when generated Db module is absent`` () =
  let tempDir = createTempDir "mig_resolve_generated_schema_missing_module"

  try
    let assembly = makeAssembly tempDir typeof<CodegenResult>.Assembly.Location

    resolveGeneratedSchema assembly
    |> assertRegularErrorContains "Compiled generated Db module with static GeneratedSchema was not found"
  finally
    Directory.Delete(tempDir, true)
