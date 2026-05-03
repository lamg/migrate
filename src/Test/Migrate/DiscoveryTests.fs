module Test.Migrate.DiscoveryTests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite

open MigLib.Migrate.Discovery
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
  { dbInstance = TestGenerated.Db.DefaultDbInstance
    dbDir = tempDir
    targetSchema = TestGenerated.Db.Schema
    dbApp = TestGenerated.Db.DbApp
    schemaIdentity = TestGenerated.Db.SchemaIdentity }

let private assertRegularErrorContains expectedText result =
  match result with
  | Error(MigError.Regular message) -> Assert.Contains(expectedText, message)
  | Error error -> failwith $"Expected MigError.Regular, got: {error}"
  | Ok value -> failwith $"Expected error, got: {value}"

let private report _ = Task.FromResult()

[<Fact>]
let ``resolveMigrationInputs resolves shared migration project state`` () =
  let tempDir = createTempDir "mig_migrate_discovery"

  try
    writeProjectLayout tempDir

    match resolveMigrationInputs (makeProject tempDir) |> fun task -> task.Result with
    | Ok projectState ->
      Assert.Equal(Path.Combine(tempDir, "generated-fixture-main-0123456789abcdef.sqlite"), projectState.targetDbPath)
      Assert.True(projectState.sourceDbPath.IsNone)
      Assert.Equal(Path.Combine(tempDir, "archive"), projectState.archiveDirectory)
      Assert.True(projectState.sourceSchema.IsNone)
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

    match resolveMigrationInputs (makeProject tempDir) |> fun task -> task.Result with
    | Ok projectState -> Assert.Equal(Some sourceDbPath, projectState.sourceDbPath)
    | Error error -> failwith $"Expected migration inputs to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveMigrationInputs loads source schema when source database exists`` () =
  let tempDir = createTempDir "mig_migrate_discovery_source_schema"

  try
    writeProjectLayout tempDir

    let sourceDbPath =
      Path.Combine(tempDir, "generated-fixture-main-fedcba9876543210.sqlite")

    use connection = new SqliteConnection($"Data Source={sourceDbPath}")
    connection.Open()

    use command =
      new SqliteCommand("CREATE TABLE generated_fixture(id INTEGER NOT NULL);", connection)

    command.ExecuteNonQuery() |> ignore
    connection.Close()

    match resolveMigrationInputs (makeProject tempDir) |> fun task -> task.Result with
    | Ok projectState ->
      Assert.True(projectState.sourceSchema.IsSome)
      Assert.Equal(Some sourceDbPath, projectState.sourceDbPath)
    | Error error -> failwith $"Expected migration inputs to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``prepareNewDb creates generated target database`` () =
  let tempDir = createTempDir "mig_migrate_prepare_new_db"

  try
    writeProjectLayout tempDir

    let expectedTargetPath =
      Path.Combine(tempDir, "generated-fixture-main-0123456789abcdef.sqlite")

    match prepareNewDb report (makeProject tempDir) |> fun task -> task.Result with
    | Ok dbPath ->
      Assert.Equal(expectedTargetPath, dbPath)
      Assert.True(File.Exists dbPath)

      use connection = new SqliteConnection($"Data Source={dbPath}")
      connection.Open()

      use command =
        new SqliteCommand("SELECT COUNT(*) FROM generated_fixture", connection)

      Assert.Equal(0L, command.ExecuteScalar() :?> int64)
    | Error error -> failwith $"Expected prepareNewDb to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``prepareNewDb fails when target database already exists`` () =
  let tempDir = createTempDir "mig_migrate_prepare_existing_target"

  try
    writeProjectLayout tempDir

    let targetPath =
      Path.Combine(tempDir, "generated-fixture-main-0123456789abcdef.sqlite")

    writeFile targetPath ""

    prepareNewDb report (makeProject tempDir)
    |> fun task -> task.Result
    |> assertRegularErrorContains "Target database already exists"
  finally
    Directory.Delete(tempDir, true)
