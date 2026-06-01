module Test.Codegen.InputsTests

open System
open System.IO

open MigLib.Codegen.Inputs
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

let private schemaAssemblyPath tempDir =
  Path.Combine(schemaDirectory tempDir, "bin", "Debug", "net10.0", "MigSchema.dll")

let private writeRuntimeProject tempDir rootNamespace =
  writeFile
    (runtimeProjectPath tempDir)
    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>{rootNamespace}</RootNamespace></PropertyGroup></Project>"

let private writeMigSchemaProject tempDir =
  writeFile (schemaProjectPath tempDir) "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

let private writeMigSchemaAssembly tempDir =
  writeFile (schemaAssemblyPath tempDir) ""

[<Fact>]
let ``resolveInputs uses project codegen conventions`` () =
  let tempDir = createTempDir "mig_codegen_inputs"

  try
    writeRuntimeProject tempDir "RuntimeRoot"
    writeMigSchemaProject tempDir
    writeMigSchemaAssembly tempDir

    match resolveInputs tempDir with
    | Ok inputs ->
      Assert.Equal(Path.GetFullPath(runtimeProjectPath tempDir), inputs.project.runtimeProjectPath)
      Assert.Equal(Path.GetFullPath(schemaAssemblyPath tempDir), inputs.schemaAssembly.assemblyPath)
      Assert.Equal(Path.Combine(tempDir, "MigSchema", "Db.fs"), inputs.outputPath)
    | Error error -> failwith $"Expected codegen inputs to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveInputs does not require project RootNamespace metadata`` () =
  let tempDir = createTempDir "mig_codegen_inputs_without_root_namespace"

  try
    writeFile (runtimeProjectPath tempDir) "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"
    writeMigSchemaProject tempDir
    writeMigSchemaAssembly tempDir

    match resolveInputs tempDir with
    | Ok inputs -> Assert.Equal(Path.GetFullPath(schemaAssemblyPath tempDir), inputs.schemaAssembly.assemblyPath)
    | Error error -> failwith $"Expected codegen inputs to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)
