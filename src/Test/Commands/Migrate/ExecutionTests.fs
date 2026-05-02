module Test.Commands.Migrate.ExecutionTests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite

open MigLib.Commands.Migrate.Execution
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
  let connection = new SqliteConnection($"Data Source={dbPath}")
  connection.Open()
  connection

let private executeSql (connection: SqliteConnection) sql =
  use cmd = new SqliteCommand(sql, connection)
  cmd.ExecuteNonQuery() |> ignore

let private scalar<'a> (connection: SqliteConnection) sql =
  use cmd = new SqliteCommand(sql, connection)
  cmd.ExecuteScalar() :?> 'a

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
  { dbInstance = TestGenerated.Db.DefaultDbInstance
    dbDir = tempDir
    targetSchema = TestGenerated.Db.Schema
    dbApp = TestGenerated.Db.DbApp
    schemaIdentity = TestGenerated.Db.SchemaIdentity }

let private report _ = Task.FromResult()

let private sourceDbPath tempDir =
  Path.Combine(tempDir, "generated-fixture-main-fedcba9876543210.sqlite")

let private targetDbPath tempDir =
  Path.Combine(tempDir, "generated-fixture-main-0123456789abcdef.sqlite")

let private archivedSourceDbPath tempDir =
  Path.Combine(tempDir, "archive", "generated-fixture-main-fedcba9876543210.sqlite")

let private assertRegularErrorContains expectedText result =
  match result with
  | Error(MigError.Regular message) -> Assert.Contains(expectedText, message)
  | Error error -> failwith $"Expected MigError.Regular, got: {error}"
  | Ok value -> failwith $"Expected error, got: {value}"

[<Fact>]
let ``migrate creates target database when no source exists`` () =
  let tempDir = createTempDir "mig_execution_no_source"

  try
    writeProjectLayout tempDir

    match migrate report (makeProject tempDir) |> fun task -> task.Result with
    | Ok result ->
      Assert.Equal(targetDbPath tempDir, result.newDbPath)
      Assert.Equal(result.newDbPath, result.db.DbPath)
      Assert.Equal(None, result.archivedOldDbPath)
      Assert.Equal(0, result.copiedTables)
      Assert.Equal(0L, result.copiedRows)
      Assert.True(File.Exists result.newDbPath)
    | Error error -> failwith $"Expected migrate to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``migrate copies compatible source rows`` () =
  let tempDir = createTempDir "mig_execution_copy_rows"

  try
    writeProjectLayout tempDir
    use sourceConnection = openConnection (sourceDbPath tempDir)
    executeSql sourceConnection "CREATE TABLE generated_fixture(id INTEGER NOT NULL);"
    executeSql sourceConnection "INSERT INTO generated_fixture(id) VALUES (1), (2), (3);"
    sourceConnection.Close()

    let messages = ResizeArray<string>()

    let report message =
      messages.Add message
      Task.FromResult()

    match migrate report (makeProject tempDir) |> fun task -> task.Result with
    | Ok result ->
      Assert.Equal(1, result.copiedTables)
      Assert.Equal(3L, result.copiedRows)
      Assert.Equal(Some(archivedSourceDbPath tempDir), result.archivedOldDbPath)
      Assert.False(File.Exists(sourceDbPath tempDir))
      Assert.True(File.Exists(archivedSourceDbPath tempDir))
      Assert.Contains(messages, fun message -> message.Contains "Copying data from source database")
      Assert.Contains(messages, fun message -> message.Contains "Copying table: generated_fixture -> generated_fixture")
      Assert.Contains(messages, fun message -> message.Contains "Copied 3 row(s) into table: generated_fixture")
      Assert.Contains(messages, fun message -> message.Contains "Copied 3 row(s) across 1 table(s).")
      Assert.Contains(messages, fun message -> message.Contains "Marking old database readonly")
      Assert.Contains(messages, fun message -> message.Contains "Archiving old database")

      use targetConnection = openConnection result.newDbPath
      Assert.Equal(3L, scalar<int64> targetConnection "SELECT COUNT(*) FROM generated_fixture")
      Assert.Equal(6L, scalar<int64> targetConnection "SELECT SUM(id) FROM generated_fixture")

      use archiveConnection = openConnection (archivedSourceDbPath tempDir)
      Assert.Equal(1L, scalar<int64> archiveConnection "SELECT COUNT(*) FROM _mig_readonly WHERE id = 1")
    | Error error -> failwith $"Expected migrate to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``migrate fails when archive target already exists`` () =
  let tempDir = createTempDir "mig_execution_archive_collision"

  try
    writeProjectLayout tempDir
    use sourceConnection = openConnection (sourceDbPath tempDir)
    executeSql sourceConnection "CREATE TABLE generated_fixture(id INTEGER NOT NULL);"
    executeSql sourceConnection "INSERT INTO generated_fixture(id) VALUES (1);"
    sourceConnection.Close()

    writeFile (archivedSourceDbPath tempDir) ""

    migrate report (makeProject tempDir)
    |> fun task -> task.Result
    |> assertRegularErrorContains "Archived database already exists"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``migrate rejects unsupported plan before creating target`` () =
  let tempDir = createTempDir "mig_execution_unsupported_plan"

  try
    writeProjectLayout tempDir
    use sourceConnection = openConnection (sourceDbPath tempDir)
    executeSql sourceConnection "CREATE TABLE generated_fixture(name TEXT);"
    sourceConnection.Close()

    migrate report (makeProject tempDir)
    |> fun task -> task.Result
    |> assertRegularErrorContains "Migration plan has unsupported differences"

    Assert.False(File.Exists(targetDbPath tempDir))
  finally
    Directory.Delete(tempDir, true)
