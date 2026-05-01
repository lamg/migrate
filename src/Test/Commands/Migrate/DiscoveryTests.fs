module Test.Commands.Migrate.DiscoveryTests

open System
open System.IO

open MigLib.Commands.Migrate.Discovery
open MigLib.Commands.Types
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
  { fsProject = runtimeProjectPath tempDir
    dbInstance = TestGenerated.Db.DefaultDbInstance
    dbDir = tempDir }

let private assertRegularErrorContains expectedText result =
  match result with
  | Error(MigError.Regular message) -> Assert.Contains(expectedText, message)
  | Error error -> failwith $"Expected MigError.Regular, got: {error}"
  | Ok value -> failwith $"Expected error, got: {value}"

[<Fact>]
let ``resolveMigrationInputs resolves generated schema and database paths`` () =
  let tempDir = createTempDir "mig_migrate_discovery"

  try
    writeProjectLayout tempDir

    match resolveMigrationInputs (makeProject tempDir) with
    | Ok(generatedSchema, databasePaths) ->
      Assert.Equal("TestGenerated.Db", generatedSchema.moduleName)
      Assert.Equal(TestGenerated.Db.DbApp, generatedSchema.generatedModule.dbApp)
      Assert.Equal(TestGenerated.Db.SchemaHash, generatedSchema.generatedModule.schemaHash)
      Assert.Equal(Path.GetFullPath(runtimeAssemblyPath tempDir), generatedSchema.assembly.assemblyPath)
      Assert.Equal(Path.Combine(tempDir, "generated-fixture-main-0123456789abcdef.sqlite"), databasePaths.targetDbPath)
      Assert.True(databasePaths.sourceDbPath.IsNone)
      Assert.Equal(Path.Combine(tempDir, "archive"), databasePaths.archiveDirectory)
    | Error error -> failwith $"Expected migration inputs to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveMigrationInputs resolves existing source database`` () =
  let tempDir = createTempDir "mig_migrate_discovery_source"

  try
    writeProjectLayout tempDir

    let sourceDbPath =
      Path.Combine(tempDir, "generated-fixture-main-fedcba9876543210.sqlite")

    writeFile sourceDbPath ""

    match resolveMigrationInputs (makeProject tempDir) with
    | Ok(_, databasePaths) -> Assert.Equal(Some sourceDbPath, databasePaths.sourceDbPath)
    | Error error -> failwith $"Expected migration inputs to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveMigrationInputs fails when runtime output is missing`` () =
  let tempDir = createTempDir "mig_migrate_discovery_missing_runtime"

  try
    writeProjectLayout tempDir
    File.Delete(runtimeAssemblyPath tempDir)

    resolveMigrationInputs (makeProject tempDir)
    |> assertRegularErrorContains "Could not resolve runtime assembly"
  finally
    Directory.Delete(tempDir, true)
