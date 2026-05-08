module Test.Status.ExecutionTests

open System
open System.IO

open MigLib.Status.Execution
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
  Path.Combine(tempDir, "DomainModeling", "DomainModeling.fsproj")

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

  writeFile (schemaProjectPath tempDir) "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

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

let private sourceDbPath tempDir =
  Path.Combine(tempDir, "generated-fixture-main-fedcba9876543210.sqlite")

let private targetDbPath tempDir =
  Path.Combine(tempDir, "generated-fixture-main-0123456789abcdef.sqlite")

let private archiveDbPath tempDir hash =
  Path.Combine(tempDir, "archive", $"generated-fixture-main-{hash}.sqlite")

let private assertRegularErrorContains expectedText result =
  match result with
  | Error(MigError.Regular message) -> Assert.Contains(expectedText, message)
  | Error error -> failwith $"Expected MigError.Regular, got: {error}"
  | Ok value -> failwith $"Expected error, got: {value}"

[<Fact>]
let ``status reports no databases`` () =
  let tempDir = createTempDir "mig_status_none"

  try
    writeProjectLayout tempDir

    match status (makeProject tempDir) |> fun task -> task.Result with
    | Ok result ->
      Assert.Equal(None, result.currentDbPath)
      Assert.Empty result.archivedDbPaths
      Assert.False result.needsMigration
    | Error error -> failwith $"Expected status to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``status reports current target database`` () =
  let tempDir = createTempDir "mig_status_target"

  try
    writeProjectLayout tempDir
    writeFile (targetDbPath tempDir) ""

    match status (makeProject tempDir) |> fun task -> task.Result with
    | Ok result ->
      Assert.Equal(Some(targetDbPath tempDir), result.currentDbPath)
      Assert.False result.needsMigration
    | Error error -> failwith $"Expected status to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``status reports old source database needs migration`` () =
  let tempDir = createTempDir "mig_status_source"

  try
    writeProjectLayout tempDir
    writeFile (sourceDbPath tempDir) ""

    match status (makeProject tempDir) |> fun task -> task.Result with
    | Ok result ->
      Assert.Equal(Some(sourceDbPath tempDir), result.currentDbPath)
      Assert.True result.needsMigration
    | Error error -> failwith $"Expected status to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``status prefers current target while pending source still needs migration`` () =
  let tempDir = createTempDir "mig_status_target_and_source"

  try
    writeProjectLayout tempDir
    writeFile (targetDbPath tempDir) ""
    writeFile (sourceDbPath tempDir) ""

    match status (makeProject tempDir) |> fun task -> task.Result with
    | Ok result ->
      Assert.Equal(Some(targetDbPath tempDir), result.currentDbPath)
      Assert.True result.needsMigration
    | Error error -> failwith $"Expected status to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``status lists archived databases`` () =
  let tempDir = createTempDir "mig_status_archives"

  try
    writeProjectLayout tempDir
    let archiveA = archiveDbPath tempDir "1111111111111111"
    let archiveB = archiveDbPath tempDir "2222222222222222"
    writeFile archiveB ""
    writeFile archiveA ""

    match status (makeProject tempDir) |> fun task -> task.Result with
    | Ok result -> Assert.Equal<string list>([ archiveA; archiveB ], result.archivedDbPaths)
    | Error error -> failwith $"Expected status to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``project resolution fails when multiple source candidates match`` () =
  let tempDir = createTempDir "mig_status_multiple_sources"

  try
    writeProjectLayout tempDir
    writeFile (Path.Combine(tempDir, "generated-fixture-main-1111111111111111.sqlite")) ""
    writeFile (Path.Combine(tempDir, "generated-fixture-main-2222222222222222.sqlite")) ""

    discoverProject tempDir (Some TestGenerated.Db.GeneratedSchema.defaultDbInstance) tempDir
    |> fun task -> task.Result
    |> assertRegularErrorContains "Found multiple candidates"
  finally
    Directory.Delete(tempDir, true)
