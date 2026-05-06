module Test.Plan.DiscoveryTests

open System
open System.IO

open MigLib.Plan.Discovery
open MigLib.Resolution.Projects
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

let private schemaProjectPath tempDir =
  Path.Combine(tempDir, "MigSchema", "MigSchema.fsproj")

let private runtimeAssemblyPath tempDir =
  let assemblyName =
    Path.GetFileNameWithoutExtension(typeof<TestGenerated.Db.Marker>.Assembly.Location)

  Path.Combine(tempDir, "bin", "Debug", "net10.0", $"{assemblyName}.dll")

let private writeProjectLayout tempDir =
  let fixtureAssembly = typeof<TestGenerated.Db.Marker>.Assembly.Location
  let assemblyName = Path.GetFileNameWithoutExtension fixtureAssembly

  writeFile
    (runtimeProjectPath tempDir)
    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>TestGenerated</RootNamespace><AssemblyName>{assemblyName}</AssemblyName></PropertyGroup></Project>"

  writeFile
    (schemaProjectPath tempDir)
    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>TestGeneratedSchema</RootNamespace></PropertyGroup></Project>"

  let targetAssemblyPath = runtimeAssemblyPath tempDir
  Directory.CreateDirectory(Path.GetDirectoryName targetAssemblyPath) |> ignore
  File.Copy(fixtureAssembly, targetAssemblyPath, true)

let private makeProject tempDir =
  match
    discoverProject tempDir (Some TestGenerated.Db.GeneratedSchema.defaultDbInstance) tempDir
    |> fun task -> task.Result
  with
  | Ok project -> project
  | Error error -> failwith $"Expected project to resolve, got: {error}"

[<Fact>]
let ``resolvePlanInputs returns resolved project`` () =
  let tempDir = createTempDir "mig_plan_discovery"

  try
    writeProjectLayout tempDir

    match resolvePlanInputs (makeProject tempDir) |> fun task -> task.Result with
    | Ok projectState ->
      Assert.Equal(Path.Combine(tempDir, "generated-fixture-main-0123456789abcdef.sqlite"), projectState.targetDbPath)
      Assert.True(projectState.sourceDbPath.IsNone)
    | Error error -> failwith $"Expected plan inputs to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)
