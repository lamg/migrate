module Test.Reset.ExecutionTests

open System
open System.IO
open Microsoft.Data.Sqlite

open MigLib.Reset.Execution
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

let private targetDbPath tempDir =
  Path.Combine(tempDir, "generated-fixture-main-0123456789abcdef.sqlite")

let private archiveDbPath tempDir hash =
  Path.Combine(tempDir, "archive", $"generated-fixture-main-{hash}.sqlite")

let private assertRegularErrorContains expectedText result =
  match result with
  | Error(MigError.Regular message) -> Assert.Contains(expectedText, message)
  | Error error -> failwith $"Expected MigError.Regular, got: {error}"
  | Ok value -> failwith $"Expected error, got: {value}"

let private createReadonlyArchive (dbPath: string) markerText =
  Directory.CreateDirectory(Path.GetDirectoryName dbPath) |> ignore
  use connection = openConnection dbPath
  executeSql connection "CREATE TABLE generated_fixture(id INTEGER NOT NULL);"
  executeSql connection "CREATE TABLE _mig_readonly(id INTEGER PRIMARY KEY CHECK (id = 1), marked_utc TEXT NOT NULL);"
  executeSql connection $"INSERT INTO _mig_readonly(id, marked_utc) VALUES (1, '{markerText}');"
  connection.Close()

[<Fact>]
let ``reset reports nothing when no current or archive exists`` () =
  let tempDir = createTempDir "mig_reset_none"

  try
    writeProjectLayout tempDir

    match reset (makeProject tempDir) |> fun task -> task.Result with
    | Ok result ->
      Assert.Equal(None, result.restoredDbPath)
      Assert.Equal(None, result.removedCurrentDbPath)
    | Error error -> failwith $"Expected reset to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``reset removes current target when no archive exists`` () =
  let tempDir = createTempDir "mig_reset_remove_target"

  try
    writeProjectLayout tempDir
    writeFile (targetDbPath tempDir) ""

    match reset (makeProject tempDir) |> fun task -> task.Result with
    | Ok result ->
      Assert.Equal(None, result.restoredDbPath)
      Assert.Equal(Some(targetDbPath tempDir), result.removedCurrentDbPath)
      Assert.False(File.Exists(targetDbPath tempDir))
    | Error error -> failwith $"Expected reset to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``reset restores archive and removes readonly marker`` () =
  let tempDir = createTempDir "mig_reset_restore_archive"

  try
    writeProjectLayout tempDir
    let archivePath = archiveDbPath tempDir "fedcba9876543210"
    let restoredPath = Path.Combine(tempDir, Path.GetFileName archivePath)
    createReadonlyArchive archivePath "old"

    match reset (makeProject tempDir) |> fun task -> task.Result with
    | Ok result ->
      Assert.Equal(Some restoredPath, result.restoredDbPath)
      Assert.Equal(None, result.removedCurrentDbPath)
      Assert.False(File.Exists archivePath)
      Assert.True(File.Exists restoredPath)

      use connection = openConnection restoredPath

      Assert.Equal(
        0L,
        scalar<int64> connection "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '_mig_readonly'"
      )
    | Error error -> failwith $"Expected reset to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``reset removes target and restores most recently modified archive`` () =
  let tempDir = createTempDir "mig_reset_latest_archive"

  try
    writeProjectLayout tempDir
    writeFile (targetDbPath tempDir) "current"

    let olderArchive = archiveDbPath tempDir "1111111111111111"
    let newerArchive = archiveDbPath tempDir "2222222222222222"
    createReadonlyArchive olderArchive "older"
    createReadonlyArchive newerArchive "newer"
    File.SetLastWriteTimeUtc(olderArchive, DateTime.UtcNow.AddHours -2.0)
    File.SetLastWriteTimeUtc(newerArchive, DateTime.UtcNow.AddHours -1.0)

    let restoredPath = Path.Combine(tempDir, Path.GetFileName newerArchive)

    match reset (makeProject tempDir) |> fun task -> task.Result with
    | Ok result ->
      Assert.Equal(Some restoredPath, result.restoredDbPath)
      Assert.Equal(Some(targetDbPath tempDir), result.removedCurrentDbPath)
      Assert.True(File.Exists restoredPath)
      Assert.True(File.Exists olderArchive)
      Assert.False(File.Exists newerArchive)
      Assert.False(File.Exists(targetDbPath tempDir))
    | Error error -> failwith $"Expected reset to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``reset fails when restore destination already exists`` () =
  let tempDir = createTempDir "mig_reset_restore_collision"

  try
    writeProjectLayout tempDir
    let archivePath = archiveDbPath tempDir "fedcba9876543210"
    createReadonlyArchive archivePath "archived"

    use existingConnection =
      openConnection (Path.Combine(tempDir, Path.GetFileName archivePath))

    existingConnection.Close()

    reset (makeProject tempDir)
    |> fun task -> task.Result
    |> assertRegularErrorContains "destination already exists"
  finally
    Directory.Delete(tempDir, true)
