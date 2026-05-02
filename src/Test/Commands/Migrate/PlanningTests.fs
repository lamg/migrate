module Test.Commands.Migrate.PlanningTests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite

open MigLib.Commands.Migrate.Planning
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

let private openConnection dbPath =
  SQLitePCL.Batteries_V2.Init()
  let connection = new SqliteConnection $"Data Source={dbPath}"
  connection.Open()
  connection

let private executeSql (connection: SqliteConnection) sql =
  use cmd = new SqliteCommand(sql, connection)
  cmd.ExecuteNonQuery() |> ignore

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
  { fsProject = runtimeProjectPath tempDir
    dbInstance = TestGenerated.Db.DefaultDbInstance
    dbDir = tempDir }

let private report _ = Task.FromResult()

[<Fact>]
let ``buildPlan can create target when no source database exists`` () =
  let tempDir = createTempDir "mig_planning_no_source"

  try
    writeProjectLayout tempDir

    match buildPlan report (makeProject tempDir) |> fun task -> task.Result with
    | Ok migrationPlan ->
      Assert.True migrationPlan.result.canMigrate
      Assert.True migrationPlan.sourceSchema.IsNone
      Assert.Equal(None, migrationPlan.result.sourceDbPath)
      Assert.Contains("no source database found", migrationPlan.result.supportedDifferences)
      Assert.Empty migrationPlan.result.unsupportedDifferences
    | Error error -> failwith $"Expected planning to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``buildPlan accepts compatible source table`` () =
  let tempDir = createTempDir "mig_planning_compatible_source"

  try
    writeProjectLayout tempDir

    let sourceDbPath =
      Path.Combine(tempDir, "generated-fixture-main-fedcba9876543210.sqlite")

    use connection = openConnection sourceDbPath
    executeSql connection "CREATE TABLE generated_fixture(id INTEGER NOT NULL);"
    connection.Close()

    match buildPlan report (makeProject tempDir) |> fun task -> task.Result with
    | Ok migrationPlan ->
      Assert.True migrationPlan.result.canMigrate
      Assert.Equal(Some sourceDbPath, migrationPlan.result.sourceDbPath)
      Assert.Empty migrationPlan.result.unsupportedDifferences
    | Error error -> failwith $"Expected planning to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``buildPlan rejects missing required target column`` () =
  let tempDir = createTempDir "mig_planning_missing_required_column"

  try
    writeProjectLayout tempDir

    let sourceDbPath =
      Path.Combine(tempDir, "generated-fixture-main-fedcba9876543210.sqlite")

    use connection = openConnection sourceDbPath
    executeSql connection "CREATE TABLE generated_fixture(name TEXT);"
    connection.Close()

    match buildPlan report (makeProject tempDir) |> fun task -> task.Result with
    | Ok migrationPlan ->
      Assert.False migrationPlan.result.canMigrate

      Assert.Contains(
        migrationPlan.result.unsupportedDifferences,
        fun difference -> difference.Contains "generated_fixture.id"
      )
    | Error error -> failwith $"Expected planning to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)
