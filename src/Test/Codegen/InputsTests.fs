module Test.Codegen.InputsTests

open System
open System.IO

open MigLib.Codegen.Inputs
open MigLib.Types
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

let private runtimeProjectPath tempDir = Path.Combine(tempDir, "Runtime.fsproj")

let private schemaDirectory tempDir = Path.Combine(tempDir, "MigSchema")

let private schemaProjectPath tempDir =
  Path.Combine(schemaDirectory tempDir, "MigSchema.fsproj")

let private schemaSourcePath tempDir =
  Path.Combine(schemaDirectory tempDir, "MigSchema.fs")

let private schemaAssemblyPath tempDir =
  Path.Combine(schemaDirectory tempDir, "bin", "Debug", "net10.0", "MigSchema.dll")

let private writeRuntimeProject tempDir rootNamespace =
  writeFile
    (runtimeProjectPath tempDir)
    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>{rootNamespace}</RootNamespace></PropertyGroup></Project>"

let private writeSchemaProject tempDir rootNamespace =
  writeFile
    (schemaProjectPath tempDir)
    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>{rootNamespace}</RootNamespace></PropertyGroup></Project>"

let private writeSchemaAssembly tempDir =
  writeFile (schemaAssemblyPath tempDir) ""

let private writeSchemaSource tempDir =
  writeFile (schemaSourcePath tempDir) "module SchemaRoot.MigSchema"

let private makeProject tempDir =
  { dbInstance = "main"
    dbDir = tempDir
    targetSchema = TestGenerated.Db.Schema
    dbApp = TestGenerated.Db.DbApp
    schemaIdentity = TestGenerated.Db.SchemaIdentity }

let private assertRegularErrorContains expectedText result =
  match result with
  | Error(MigError.Regular message) -> Assert.Contains(expectedText, message)
  | Error error -> failwith $"Expected MigError.Regular, got: {error}"
  | Ok value -> failwith $"Expected error, got: {value}"

[<Fact>]
let ``resolveInputs uses project codegen conventions`` () =
  let tempDir = createTempDir "mig_codegen_inputs"

  try
    writeRuntimeProject tempDir "RuntimeRoot"
    writeSchemaProject tempDir "SchemaRoot"
    writeSchemaSource tempDir
    writeSchemaAssembly tempDir

    match resolveInputs (makeProject tempDir) with
    | Ok inputs ->
      Assert.Equal(Path.GetFullPath(runtimeProjectPath tempDir), inputs.project.runtimeProjectPath)
      Assert.Equal(Path.GetFullPath(schemaAssemblyPath tempDir), inputs.schemaAssembly.assemblyPath)
      Assert.Equal("SchemaRoot.MigSchema", inputs.schemaModuleName)
      Assert.Equal("RuntimeRoot.Db", inputs.generatedModuleName)
      Assert.Equal(Path.GetFullPath(schemaSourcePath tempDir), inputs.schemaSourcePath)
      Assert.Equal("RuntimeRoot", inputs.dbApp)
      Assert.Equal(Path.Combine(tempDir, "MigSchema", "Db.fs"), inputs.outputPath)
    | Error error -> failwith $"Expected codegen inputs to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveInputs fails when runtime RootNamespace is missing`` () =
  let tempDir = createTempDir "mig_codegen_inputs_missing_runtime_root"

  try
    writeFile (runtimeProjectPath tempDir) "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"
    writeSchemaProject tempDir "SchemaRoot"
    writeSchemaSource tempDir
    writeSchemaAssembly tempDir

    resolveInputs (makeProject tempDir)
    |> assertRegularErrorContains "runtime project"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveInputs fails when schema RootNamespace is missing`` () =
  let tempDir = createTempDir "mig_codegen_inputs_missing_schema_root"

  try
    writeRuntimeProject tempDir "RuntimeRoot"
    writeFile (schemaProjectPath tempDir) "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"
    writeSchemaSource tempDir
    writeSchemaAssembly tempDir

    resolveInputs (makeProject tempDir)
    |> assertRegularErrorContains "schema project"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveInputs fails when MigSchema source is missing`` () =
  let tempDir = createTempDir "mig_codegen_inputs_missing_schema_source"

  try
    writeRuntimeProject tempDir "RuntimeRoot"
    writeSchemaProject tempDir "SchemaRoot"
    writeSchemaAssembly tempDir

    resolveInputs (makeProject tempDir)
    |> assertRegularErrorContains "Schema source file was not found"
  finally
    Directory.Delete(tempDir, true)
