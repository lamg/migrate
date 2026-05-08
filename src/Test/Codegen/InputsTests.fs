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

let private domainModelingDirectory tempDir = Path.Combine(tempDir, "DomainModeling")

let private domainModelingProjectPath tempDir =
  Path.Combine(domainModelingDirectory tempDir, "DomainModeling.fsproj")

let private schemaSourcePath tempDir =
  Path.Combine(domainModelingDirectory tempDir, "MigSchema.fs")

let private domainModelingAssemblyPath tempDir =
  Path.Combine(domainModelingDirectory tempDir, "bin", "Debug", "net10.0", "DomainModeling.dll")

let private writeRuntimeProject tempDir rootNamespace =
  writeFile
    (runtimeProjectPath tempDir)
    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>{rootNamespace}</RootNamespace></PropertyGroup></Project>"

let private writeDomainModelingProject tempDir =
  writeFile (domainModelingProjectPath tempDir) "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

let private writeDomainModelingAssembly tempDir =
  writeFile (domainModelingAssemblyPath tempDir) ""

let private writeSchemaSource tempDir =
  writeFile (schemaSourcePath tempDir) "module SchemaRoot.MigSchema"

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
    writeDomainModelingProject tempDir
    writeSchemaSource tempDir
    writeDomainModelingAssembly tempDir

    match resolveInputs tempDir with
    | Ok inputs ->
      Assert.Equal(Path.GetFullPath(runtimeProjectPath tempDir), inputs.project.runtimeProjectPath)
      Assert.Equal(Path.GetFullPath(domainModelingAssemblyPath tempDir), inputs.domainModelingAssembly.assemblyPath)
      Assert.Equal(Path.GetFullPath(schemaSourcePath tempDir), inputs.schemaSourcePath)
      Assert.Equal(Path.Combine(tempDir, "DomainModeling", "Db.fs"), inputs.outputPath)
    | Error error -> failwith $"Expected codegen inputs to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveInputs does not require project RootNamespace metadata`` () =
  let tempDir = createTempDir "mig_codegen_inputs_without_root_namespace"

  try
    writeFile (runtimeProjectPath tempDir) "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"
    writeDomainModelingProject tempDir
    writeSchemaSource tempDir
    writeDomainModelingAssembly tempDir

    match resolveInputs tempDir with
    | Ok inputs ->
      Assert.Equal(Path.GetFullPath(domainModelingAssemblyPath tempDir), inputs.domainModelingAssembly.assemblyPath)
    | Error error -> failwith $"Expected codegen inputs to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveInputs fails when DomainModeling MigSchema source is missing`` () =
  let tempDir = createTempDir "mig_codegen_inputs_missing_schema_source"

  try
    writeRuntimeProject tempDir "RuntimeRoot"
    writeDomainModelingProject tempDir
    writeDomainModelingAssembly tempDir

    resolveInputs tempDir
    |> assertRegularErrorContains "Schema source file was not found"
  finally
    Directory.Delete(tempDir, true)
