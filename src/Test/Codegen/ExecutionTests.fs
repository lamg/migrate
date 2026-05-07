module Test.Codegen.ExecutionTests

open System
open System.IO

open MigLib.Codegen.Execution
open MigLib.Codegen.Inputs
open MigLib.Types
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

let private makeInputs tempDir schemaModuleName =
  let runtimeProjectPath = Path.Combine(tempDir, "Runtime.fsproj")
  let schemaDirectory = Path.Combine(tempDir, "MigSchema")
  let schemaProjectPath = Path.Combine(schemaDirectory, "MigSchema.fsproj")
  let schemaSourcePath = Path.Combine(schemaDirectory, "MigSchema.fs")
  let outputPath = Path.Combine(tempDir, "Db.fs")

  writeFile
    runtimeProjectPath
    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>RuntimeRoot</RootNamespace></PropertyGroup></Project>"

  writeFile
    schemaProjectPath
    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>TestCodegenSchema</RootNamespace></PropertyGroup></Project>"

  writeFile schemaSourcePath "module TestCodegenSchema.MigSchema"

  let project =
    { runtimeProjectPath = runtimeProjectPath
      runtimeProjectDirectory = tempDir
      runtimeProjectName = "Runtime"
      schemaProjectPath = schemaProjectPath
      schemaDirectory = schemaDirectory }

  { project = project
    schemaAssembly =
      { project = project
        assemblyName = "Test"
        assemblyPath = typeof<TestCodegenSchema.MigSchema.Marker>.Assembly.Location }
    schemaModuleName = schemaModuleName
    generatedModuleName = "RuntimeRoot.Db"
    schemaSourcePath = schemaSourcePath
    dbApp = "RuntimeRoot"
    outputPath = outputPath }

let private assertRegularErrorContains expectedText result =
  match result with
  | Error(MigError.Regular message) -> Assert.Contains(expectedText, message)
  | Error error -> failwith $"Expected MigError.Regular, got: {error}"
  | Ok value -> failwith $"Expected error, got: {value}"

[<Fact>]
let ``runCodegen writes Db fs with metadata types and CRUD helpers`` () =
  let tempDir = createTempDir "mig_codegen_execution"

  try
    let inputs = makeInputs tempDir "TestCodegenSchema.MigSchema"

    match runCodegen inputs with
    | Ok result ->
      Assert.Equal(Path.Combine(tempDir, "Db.fs"), result.outputPath)
      Assert.Equal("RuntimeRoot.Db", result.generatedModuleName)
      Assert.Equal<string list>([ Path.Combine(tempDir, "Db.fs") ], result.generatedFiles)

      let generated = File.ReadAllText result.outputPath

      Assert.Contains("module RuntimeRoot.Db", generated)
      Assert.Contains("open MigLib.Generated", generated)
      Assert.DoesNotContain("open MigLib.Codegen.Helpers", generated)
      Assert.DoesNotContain("open MigLib.Runtime", generated)
      Assert.Contains("let GeneratedSchema", generated)
      Assert.Contains("dbApp = \"RuntimeRoot\"", generated)
      Assert.Contains("defaultDbInstance = \"main\"", generated)
      Assert.DoesNotContain("DbFileForInstance", generated)
      Assert.DoesNotContain("let DbFile", generated)
      Assert.Contains("schemaHash =", generated)
      Assert.DoesNotContain("SchemaIdentity", generated)
      Assert.Contains("schema =", generated)
      Assert.Contains("codegen_fixture", generated)
      Assert.Contains("type CodegenFixture =", generated)
      Assert.Contains("type CodegenFixtureView =", generated)
      Assert.Contains("type NewPerson =", generated)
      Assert.Contains("type Person =", generated)
      Assert.Contains("static member Insert", generated)
      Assert.Contains("static member InsertOrIgnore", generated)
      Assert.Contains("static member SelectById", generated)
      Assert.Contains("static member SelectAll", generated)
      Assert.Contains("static member SelectOne", generated)
      Assert.Contains("static member Update", generated)
      Assert.Contains("static member Delete", generated)
      Assert.Contains("static member DeleteAll", generated)
      Assert.Contains("static member SelectByName", generated)
      Assert.Contains("static member SelectNameLike", generated)
      Assert.Contains("static member SelectByNameOrInsert", generated)
      Assert.Contains("static member Upsert", generated)
      Assert.Contains("Recording.recordInsert", generated)
    | Error error -> failwith $"Expected codegen to run, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``runCodegen fails when schema module is missing`` () =
  let tempDir = createTempDir "mig_codegen_execution_missing_schema_module"

  try
    let inputs = makeInputs tempDir "MissingSchema.MigSchema"

    runCodegen inputs
    |> assertRegularErrorContains "Compiled schema module 'MissingSchema.MigSchema' was not found"
  finally
    Directory.Delete(tempDir, true)
