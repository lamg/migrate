module Tests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization.Metadata
open System.Threading
open System.Threading.Tasks
open MigLib.Build
open MigLib.CompiledSchema
open MigLib.Db
open MigLib.Util
open MigLib.Web
open Mig.CodeGen.CodeGen
open Mig.DeclarativeMigrations.Types
open Mig.DeclarativeMigrations.DataCopy
open Mig.DeclarativeMigrations.DrainReplay
open Mig.DeclarativeMigrations.DrainReplayTypes
open Mig.DeclarativeMigrations.SchemaDiff
open Mig.HotMigration
open Mig.HotMigrationTypes
open Mig.SchemaReflection
open Microsoft.AspNetCore.Http
open Microsoft.Data.Sqlite
open Xunit

let private mkColumn name columnType constraints =
  { name = name
    previousName = None
    columnType = columnType
    constraints = constraints
    enumLikeDu = None
    unitOfMeasure = None }

[<Fact>]
let ``txn builder handles deep recursive bind chains without stack overflow`` () =
  let rec buildSteps remaining current =
    txn {
      if remaining = 0 then
        return current
      else
        let! next = txn { return current + 1 }
        return! buildSteps (remaining - 1) next
    }

  let result =
    buildSteps 50000 0 Unchecked.defaultof<SqliteTransaction>
    |> fun task -> task.GetAwaiter().GetResult()

  match result with
  | Ok value -> Assert.Equal(50000, value)
  | Error ex -> failwith ex.Message

let private mkTable name columns constraints =
  { name = name
    previousName = None
    dropColumns = []
    columns = columns
    constraints = constraints
    queryByAnnotations = []
    queryLikeAnnotations = []
    queryByOrCreateAnnotations = []
    selectOneAnnotations = []
    insertOrIgnoreAnnotations = []
    deleteAllAnnotations = []
    upsertAnnotations = [] }

let private mkForeignKey refTable refColumns =
  ForeignKey
    { columns = []
      refTable = refTable
      refColumns = refColumns
      onDelete = None
      onUpdate = None }

let private mkRow pairs = pairs |> Map.ofList

let private mkLogEntry id txnId ordering operation sourceTable rowData =
  { id = id
    txnId = txnId
    ordering = ordering
    operation = operation
    sourceTable = sourceTable
    rowData = rowData }

let private cliIoLock = obj ()

let private runMigCliInDirectory (workingDirectory: string option) (args: string list) =
  lock cliIoLock (fun () ->
    let originalOut = Console.Out
    let originalErr = Console.Error
    let originalDirectory = Directory.GetCurrentDirectory()
    use outWriter = new StringWriter()
    use errWriter = new StringWriter()
    Console.SetOut outWriter
    Console.SetError errWriter

    try
      match workingDirectory with
      | Some dir -> Directory.SetCurrentDirectory dir
      | None -> ()

      let exitCode = Mig.Program.main (args |> List.toArray)
      exitCode, outWriter.ToString(), errWriter.ToString()
    finally
      Directory.SetCurrentDirectory originalDirectory
      Console.SetOut originalOut
      Console.SetError originalErr)

let private runMigCli (args: string list) = runMigCliInDirectory None args

let private withProcessStateLock (f: unit -> 'a) = lock cliIoLock f

let private createTempFsprojWithBuildOutput projectName (sourceBuildOutputDirectory: string) =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_project_autodiscovery_{projectName}_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let projectPath = Path.Combine(tempDir, $"{projectName}.fsproj")
  File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")

  let targetBuildOutputDirectory = Path.Combine(tempDir, "bin", "Debug", "net10.0")
  Directory.CreateDirectory targetBuildOutputDirectory |> ignore

  Directory.GetFiles sourceBuildOutputDirectory
  |> Array.iter (fun sourcePath ->
    let destinationPath =
      Path.Combine(targetBuildOutputDirectory, Path.GetFileName sourcePath)

    File.Copy(sourcePath, destinationPath, true))

  tempDir

let private deriveShortSchemaHashFromSourceFile (schemaPath: string) =
  let normalizeLineEndings (text: string) =
    text.Replace("\r\n", "\n").Replace("\r", "\n")

  let normalizedSchema = File.ReadAllText schemaPath |> normalizeLineEndings
  use sha256 = SHA256.Create()
  let schemaBytes = Encoding.UTF8.GetBytes normalizedSchema
  let hashBytes = sha256.ComputeHash schemaBytes
  Convert.ToHexString(hashBytes).ToLowerInvariant().Substring(0, 16)

let private deriveDeterministicNewDbPathFromSchemaFile (directoryPath: string) (schemaPath: string) =
  let dbFileNamePrefix = DirectoryInfo(directoryPath).Name

  match deriveSchemaBoundDbFileName dbFileNamePrefix None schemaPath with
  | Ok dbFileName -> Path.Combine(directoryPath, dbFileName)
  | Error error -> failwith $"Expected schema path to produce a deterministic DB file name, got: {error}"

let private compiledFixtureDbFile = CompiledSchemaFixture.DbFileForInstance None

let private loadSchemaIdentityFromSourceFile (schemaPath: string) =
  { schemaHash = deriveShortSchemaHashFromSourceFile schemaPath
    schemaCommit = None }

let private studentSchema =
  { emptyFile with
      tables =
        [ mkTable
            "student"
            [ mkColumn
                "id"
                SqlInteger
                [ NotNull
                  PrimaryKey
                    { constraintName = None
                      columns = []
                      isAutoincrement = true } ]
              mkColumn "name" SqlText [ NotNull ] ]
            [] ] }

let private accountInvoiceSchema =
  { emptyFile with
      tables =
        [ mkTable
            "account"
            [ mkColumn
                "id"
                SqlInteger
                [ NotNull
                  PrimaryKey
                    { constraintName = None
                      columns = []
                      isAutoincrement = true } ]
              mkColumn "name" SqlText [ NotNull ] ]
            []
          mkTable
            "invoice"
            [ mkColumn
                "id"
                SqlInteger
                [ NotNull
                  PrimaryKey
                    { constraintName = None
                      columns = []
                      isAutoincrement = true } ]
              mkColumn "account_id" SqlInteger [ NotNull; mkForeignKey "account" [ "id" ] ]
              mkColumn "total" SqlReal [ NotNull ] ]
            [] ] }

let private findGitRootOrFail (startDirectory: string) =
  let rec loop (currentDirectory: string) =
    if Directory.Exists(Path.Combine(currentDirectory, ".git")) then
      Some currentDirectory
    else
      let parent = Directory.GetParent currentDirectory

      if isNull parent then None else loop parent.FullName

  match loop startDirectory with
  | Some gitRoot -> gitRoot
  | None -> failwith $"Could not locate git repository root from: {startDirectory}"

let private gitHeadCommitOrFail (repositoryDirectory: string) =
  let startInfo = ProcessStartInfo()
  startInfo.FileName <- "git"
  startInfo.UseShellExecute <- false
  startInfo.RedirectStandardOutput <- true
  startInfo.RedirectStandardError <- true
  startInfo.CreateNoWindow <- true
  startInfo.ArgumentList.Add "-C"
  startInfo.ArgumentList.Add repositoryDirectory
  startInfo.ArgumentList.Add "rev-parse"
  startInfo.ArgumentList.Add "HEAD"

  use proc = Process.Start startInfo

  if isNull proc then
    failwith "Could not start git process to resolve HEAD commit."
  else
    let output = proc.StandardOutput.ReadToEnd()
    let errorOutput = proc.StandardError.ReadToEnd()
    let exited = proc.WaitForExit 2000

    if not exited then
      failwith "git rev-parse HEAD timed out."
    elif proc.ExitCode <> 0 then
      failwith $"git rev-parse HEAD failed: {errorOutput}"
    else
      let commit = output.Trim()

      if String.IsNullOrWhiteSpace commit then
        failwith "git rev-parse HEAD returned an empty commit hash."
      else
        commit

type private TestWebEnv =
  { dbRuntime: DbRuntime
    fixedNow: DateTimeOffset }

  interface IHasDbRuntime with
    member this.DbRuntime = this.dbRuntime

  interface IClock with
    member this.UtcNow() = this.fixedNow

    member this.UtcNowRfc3339() =
      this.fixedNow.ToUniversalTime().ToString "O"

    member this.UtcNowPlusDaysRfc3339(days: float) =
      this.fixedNow.AddDays days |> fun value -> value.ToUniversalTime().ToString "O"

let private createTestWebEnv (dbPath: string) =
  { dbRuntime = dbRuntime dbPath
    fixedNow = DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero) }

let private createTestHttpContext () =
  let ctx = DefaultHttpContext()
  ctx.Response.Body <- new MemoryStream()
  ctx

type TestJsonResponse() =
  member val Id = 0L with get, set

let private assertCliHelpOutput (args: string list) (expectedUsage: string) (expectedFragments: string list) =
  let exitCode, stdOut, stdErr = runMigCli args
  Assert.Equal(1, exitCode)
  Assert.Contains(expectedUsage, stdOut)

  expectedFragments
  |> List.iter (fun fragment -> Assert.Contains(fragment, stdOut))

  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")

let private createDatabaseWithMigrationStatus (dbPath: string) (status: string option) =
  use connection = openSqliteConnection dbPath

  match status with
  | None -> ()
  | Some value ->
    [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
      $"INSERT INTO _migration_status(id, status) VALUES (0, '{value}');" ]
    |> List.iter (fun sql ->
      use cmd = new SqliteCommand(sql, connection)
      cmd.ExecuteNonQuery() |> ignore)

  connection.Close()

let private createStudentDatabase (dbPath: string) (studentNames: string list) (status: string option) =
  use connection = openSqliteConnection dbPath

  [ "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, connection)
    cmd.ExecuteNonQuery() |> ignore)

  for studentName in studentNames do
    use insertCmd =
      new SqliteCommand("INSERT INTO student(name) VALUES (@name)", connection)

    insertCmd.Parameters.AddWithValue("@name", studentName) |> ignore
    insertCmd.ExecuteNonQuery() |> ignore

  match status with
  | None -> ()
  | Some value ->
    [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
      $"INSERT INTO _migration_status(id, status) VALUES (0, '{value}');" ]
    |> List.iter (fun sql ->
      use cmd = new SqliteCommand(sql, connection)
      cmd.ExecuteNonQuery() |> ignore)

  connection.Close()

let private countStudentRows (dbPath: string) =
  use connection = openSqliteConnection dbPath
  use cmd = new SqliteCommand("SELECT COUNT(*) FROM student", connection)
  cmd.ExecuteScalar() :?> int64

[<PreviousName("legacy_student")>]
[<AutoIncPK "id">]
type private ReflectionStudentWithPreviousName =
  { id: int64
    [<PreviousName("full_name")>]
    name: string }

[<DropColumn("legacy_note")>]
[<AutoIncPK "id">]
type private ReflectionStudentWithDroppedColumn = { id: int64; name: string }

[<Fact>]
let ``resolveDatabaseFilePath combines directory and generated file name`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_resolve_file_path_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let resolvedPath =
    match resolveDatabaseFilePath tempDir "marketdesk-1111222233334444.sqlite" with
    | Ok resolvedPath -> resolvedPath
    | Error message -> failwith $"Expected database file path resolution to succeed, got error: {message}"

  Assert.Equal(Path.Combine(Path.GetFullPath tempDir, "marketdesk-1111222233334444.sqlite"), resolvedPath)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``getStartupDatabaseDecision returns migrate when target database is missing`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_startup_decision_missing_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbFileName = "marketdesk-1111222233334444.sqlite"
  let expectedPath = Path.Combine(Path.GetFullPath tempDir, dbFileName)

  let decisionResult =
    getStartupDatabaseDecision tempDir dbFileName |> fun t -> t.Result

  match decisionResult with
  | Error ex -> failwith $"Expected startup decision to succeed, got error: {ex.Message}"
  | Ok(MigrateThisInstance dbPath) -> Assert.Equal(expectedPath, dbPath)
  | Ok other -> failwith $"Expected startup decision to request migration, got: {other}"

  Directory.Delete(tempDir, true)

[<Fact>]
let ``getStartupDatabaseDecision returns use existing when target database is ready without status table`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_startup_decision_ready_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbFileName = "marketdesk-1111222233334444.sqlite"
  let dbPath = Path.Combine(tempDir, dbFileName)
  createStudentDatabase dbPath [ "Alice" ] None

  let decisionResult =
    getStartupDatabaseDecision tempDir dbFileName |> fun t -> t.Result

  match decisionResult with
  | Error ex -> failwith $"Expected startup decision to succeed, got error: {ex.Message}"
  | Ok(UseExisting resolvedPath) -> Assert.Equal(Path.GetFullPath dbPath, resolvedPath)
  | Ok other -> failwith $"Expected startup decision to use existing database, got: {other}"

  Directory.Delete(tempDir, true)

[<Fact>]
let ``getStartupDatabaseDecision returns wait when target database is still migrating`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_startup_decision_wait_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbFileName = "marketdesk-1111222233334444.sqlite"
  let dbPath = Path.Combine(tempDir, dbFileName)
  createDatabaseWithMigrationStatus dbPath (Some "migrating")

  let decisionResult =
    getStartupDatabaseDecision tempDir dbFileName |> fun t -> t.Result

  match decisionResult with
  | Error ex -> failwith $"Expected startup decision to succeed, got error: {ex.Message}"
  | Ok(WaitForMigration resolvedPath) -> Assert.Equal(Path.GetFullPath dbPath, resolvedPath)
  | Ok other -> failwith $"Expected startup decision to wait, got: {other}"

  Directory.Delete(tempDir, true)

[<Fact>]
let ``getStartupDatabaseDecision returns exit early when target database status row is missing`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_startup_decision_invalid_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbFileName = "marketdesk-1111222233334444.sqlite"
  let dbPath = Path.Combine(tempDir, dbFileName)

  use connection = openSqliteConnection dbPath

  use cmd =
    new SqliteCommand(
      "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);",
      connection
    )

  cmd.ExecuteNonQuery() |> ignore
  connection.Close()

  let decisionResult =
    getStartupDatabaseDecision tempDir dbFileName |> fun t -> t.Result

  match decisionResult with
  | Error ex -> failwith $"Expected startup decision to succeed, got error: {ex.Message}"
  | Ok(ExitEarly(resolvedPath, reason)) ->
    Assert.Equal(Path.GetFullPath dbPath, resolvedPath)
    Assert.Contains("no status row", reason)
  | Ok other -> failwith $"Expected startup decision to exit early, got: {other}"

  Directory.Delete(tempDir, true)

[<Fact>]
let ``inferPreviousDatabasePath returns none when only target database exists`` () =
  let envDir =
    Path.Combine(Path.GetTempPath(), $"mig_infer_previous_none_{Guid.NewGuid()}")

  Directory.CreateDirectory envDir |> ignore

  let dbFileName = "marketdesk-1111222233334444.sqlite"
  let dbPath = Path.Combine(envDir, dbFileName)
  createStudentDatabase dbPath [ "Alice" ] None

  let inferenceResult =
    withProcessStateLock (fun () -> inferPreviousDatabasePath envDir dbFileName)

  match inferenceResult with
  | Error ex -> failwith $"Expected previous-database inference to succeed, got: {ex.Message}"
  | Ok None -> ()
  | Ok(Some path) -> failwith $"Expected no previous database, got: {path}"

  Directory.Delete(envDir, true)

[<Fact>]
let ``inferPreviousDatabasePath returns the only non-target sqlite file`` () =
  let envDir =
    Path.Combine(Path.GetTempPath(), $"mig_infer_previous_single_{Guid.NewGuid()}")

  Directory.CreateDirectory envDir |> ignore

  let dbFileName = "marketdesk-1111222233334444.sqlite"
  let targetDbPath = Path.Combine(envDir, dbFileName)
  let oldDbPath = Path.Combine(envDir, "marketdesk-0000111122223333.sqlite")
  createStudentDatabase targetDbPath [ "Alice" ] None
  createStudentDatabase oldDbPath [ "Bob" ] None

  let inferenceResult =
    withProcessStateLock (fun () -> inferPreviousDatabasePath envDir dbFileName)

  match inferenceResult with
  | Error ex -> failwith $"Expected previous-database inference to succeed, got: {ex.Message}"
  | Ok(Some path) -> Assert.Equal(Path.GetFullPath oldDbPath, path)
  | Ok None -> failwith "Expected previous database to be inferred."

  Directory.Delete(envDir, true)

[<Fact>]
let ``inferPreviousDatabasePath fails when multiple non-target sqlite files exist`` () =
  let envDir =
    Path.Combine(Path.GetTempPath(), $"mig_infer_previous_multiple_{Guid.NewGuid()}")

  Directory.CreateDirectory envDir |> ignore

  let dbFileName = "marketdesk-1111222233334444.sqlite"
  let targetDbPath = Path.Combine(envDir, dbFileName)
  let firstOldDbPath = Path.Combine(envDir, "marketdesk-0000111122223333.sqlite")
  let secondOldDbPath = Path.Combine(envDir, "marketdesk-9999888877776666.sqlite")
  createStudentDatabase targetDbPath [ "Alice" ] None
  createStudentDatabase firstOldDbPath [ "Bob" ] None
  createStudentDatabase secondOldDbPath [ "Carol" ] None

  let inferenceResult =
    withProcessStateLock (fun () -> inferPreviousDatabasePath envDir dbFileName)

  match inferenceResult with
  | Ok result -> failwith $"Expected previous-database inference to fail, got: {result}"
  | Error ex ->
    Assert.Contains(Path.GetFullPath targetDbPath, ex.Message)
    Assert.Contains(Path.GetFullPath firstOldDbPath, ex.Message)
    Assert.Contains(Path.GetFullPath secondOldDbPath, ex.Message)

[<Fact>]
let ``inferPreviousDatabasePath ignores sqlite files with other prefixes`` () =
  let envDir =
    Path.Combine(Path.GetTempPath(), $"mig_infer_previous_other_prefix_{Guid.NewGuid()}")

  Directory.CreateDirectory envDir |> ignore

  let dbFileName = "marketdesk-1111222233334444.sqlite"
  let targetDbPath = Path.Combine(envDir, dbFileName)
  let matchingOldDbPath = Path.Combine(envDir, "marketdesk-0000111122223333.sqlite")
  let unrelatedDbPath = Path.Combine(envDir, "inventory-aaaaaaaaaaaaaaaa.sqlite")
  createStudentDatabase targetDbPath [ "Alice" ] None
  createStudentDatabase matchingOldDbPath [ "Bob" ] None
  createStudentDatabase unrelatedDbPath [ "Carol" ] None

  let inferenceResult =
    withProcessStateLock (fun () -> inferPreviousDatabasePath envDir dbFileName)

  match inferenceResult with
  | Error ex -> failwith $"Expected previous-database inference to succeed, got: {ex.Message}"
  | Ok(Some path) -> Assert.Equal(Path.GetFullPath matchingOldDbPath, path)
  | Ok None -> failwith "Expected previous database to be inferred."

  Directory.Delete(envDir, true)

[<Fact>]
let ``startService returns a DbTxnBuilder for an already ready target database`` () =
  let envDir =
    Path.Combine(Path.GetTempPath(), $"mig_start_service_ready_{Guid.NewGuid()}")

  Directory.CreateDirectory envDir |> ignore

  let dbFileName = "marketdesk-1111222233334444.sqlite"
  let dbPath = Path.Combine(envDir, dbFileName)
  createStudentDatabase dbPath [ "Alice" ] None

  let result =
    withProcessStateLock (fun () ->
      startService
        envDir
        dbFileName
        { schemaHash = "1111222233334444"
          schemaCommit = None }
        studentSchema
        CancellationToken.None
      |> fun t -> t.Result)

  match result with
  | Error ex -> failwith $"Expected startService to succeed, got error: {ex.Message}"
  | Ok builder -> Assert.Equal(Path.GetFullPath dbPath, builder.DbPath)

  Directory.Delete(envDir, true)

[<Fact>]
let ``startService initializes the target database when no previous database exists`` () =
  let envDir =
    Path.Combine(Path.GetTempPath(), $"mig_start_service_init_{Guid.NewGuid()}")

  Directory.CreateDirectory envDir |> ignore

  let dbFileName = "marketdesk-1111222233334444.sqlite"
  let expectedPath = Path.Combine(Path.GetFullPath envDir, dbFileName)

  let result =
    withProcessStateLock (fun () ->
      startService
        envDir
        dbFileName
        { schemaHash = "1111222233334444"
          schemaCommit = None }
        studentSchema
        CancellationToken.None
      |> fun t -> t.Result)

  match result with
  | Error ex -> failwith $"Expected startService to initialize the target database, got error: {ex.Message}"
  | Ok builder ->
    Assert.Equal(expectedPath, builder.DbPath)
    Assert.True(File.Exists expectedPath)
    Assert.Equal(0L, countStudentRows expectedPath)

  Directory.Delete(envDir, true)

[<Fact>]
let ``startService migrates the previous database into the target database`` () =
  let envDir =
    Path.Combine(Path.GetTempPath(), $"mig_start_service_migrate_{Guid.NewGuid()}")

  Directory.CreateDirectory envDir |> ignore

  let oldDbPath = Path.Combine(envDir, "marketdesk-previous.sqlite")
  createStudentDatabase oldDbPath [ "Alice"; "Bob" ] None

  let dbFileName = "marketdesk-1111222233334444.sqlite"
  let expectedPath = Path.Combine(Path.GetFullPath envDir, dbFileName)

  let result =
    withProcessStateLock (fun () ->
      startService
        envDir
        dbFileName
        { schemaHash = "1111222233334444"
          schemaCommit = None }
        studentSchema
        CancellationToken.None
      |> fun t -> t.Result)

  match result with
  | Error ex -> failwith $"Expected startService to migrate the previous database, got error: {ex.Message}"
  | Ok builder ->
    Assert.Equal(expectedPath, builder.DbPath)
    Assert.True(File.Exists expectedPath)
    Assert.Equal(2L, countStudentRows expectedPath)

  Directory.Delete(envDir, true)

[<Fact>]
let ``waitForStartupDatabaseReady returns when migrating database becomes ready`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_startup_wait_ready_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "marketdesk-1111222233334444.sqlite")
  createDatabaseWithMigrationStatus dbPath (Some "migrating")

  let promoteTask =
    Task.Run(fun () ->
      task {
        do! Task.Delay 100
        use connection = openSqliteConnection dbPath

        use cmd =
          new SqliteCommand("UPDATE _migration_status SET status = 'ready' WHERE id = 0", connection)

        cmd.ExecuteNonQuery() |> ignore
      }
      :> Task)

  let waitResult =
    waitForStartupDatabaseReady dbPath (TimeSpan.FromMilliseconds 10.0) CancellationToken.None
    |> fun t -> t.Result

  promoteTask.Wait()

  match waitResult with
  | Ok() -> ()
  | Error ex -> failwith $"Expected wait for ready to succeed, got: {ex.Message}"

  Directory.Delete(tempDir, true)

[<Fact>]
let ``runInitWithSchema creates and seeds database without schema script input`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_runinit_with_schema_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "init.db")

  let schema =
    { emptyFile with
        tables =
          [ mkTable
              "student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              [] ]
        inserts =
          [ { table = "student"
              columns = [ "name" ]
              values = [ [ String "Alice" ]; [ String "Bob" ] ] } ] }

  let initResult = runInitWithSchema schema dbPath |> fun t -> t.Result

  match initResult with
  | Error ex -> failwith $"Expected runInitWithSchema to succeed, got: {ex.Message}"
  | Ok result ->
    Assert.Equal(dbPath, result.newDbPath)
    Assert.Equal(2L, result.seededRows)

  use verifyConn = openSqliteConnection dbPath
  use countCmd = new SqliteCommand("SELECT COUNT(*) FROM student", verifyConn)
  let count = countCmd.ExecuteScalar() |> Convert.ToInt64
  Assert.Equal(2L, count)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``runMigrateWithSchema migrates data without schema script input`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_runmigrate_with_schema_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let newDbPath = Path.Combine(tempDir, "new.db")

  use setupOldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "INSERT INTO student(name) VALUES ('Alice');"
    "INSERT INTO student(name) VALUES ('Bob');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupOldConn.Close()

  let schema =
    { emptyFile with
        tables =
          [ mkTable
              "student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              [] ] }

  let schemaIdentity =
    { schemaHash = "1111222233334444"
      schemaCommit = Some "abc123" }

  let migrateResult =
    runMigrateWithSchema oldDbPath schemaIdentity schema newDbPath
    |> fun t -> t.Result

  match migrateResult with
  | Error ex -> failwith $"Expected runMigrateWithSchema to succeed, got: {ex.Message}"
  | Ok result ->
    Assert.Equal(newDbPath, result.newDbPath)
    Assert.Equal(1, result.copiedTables)
    Assert.Equal(2L, result.copiedRows)

  use verifyNewConn = openSqliteConnection newDbPath
  use countCmd = new SqliteCommand("SELECT COUNT(*) FROM student", verifyNewConn)
  let count = countCmd.ExecuteScalar() |> Convert.ToInt64
  Assert.Equal(2L, count)

  use identityCmd =
    new SqliteCommand("SELECT schema_hash, schema_commit FROM _schema_identity WHERE id = 0", verifyNewConn)

  use reader = identityCmd.ExecuteReader()
  Assert.True(reader.Read())
  Assert.Equal("1111222233334444", reader.GetString 0)
  Assert.Equal("abc123", reader.GetString 1)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``getMigratePlanWithSchema uses provided schema identity without schema script input`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_getplan_with_schema_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let newDbPath = Path.Combine(tempDir, "new.db")

  use setupOldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "INSERT INTO student(name) VALUES ('Alice');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupOldConn.Close()

  let schema =
    { emptyFile with
        tables =
          [ mkTable
              "student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              [] ] }

  let schemaIdentity =
    { schemaHash = "abcdef0123456789"
      schemaCommit = Some "deadbeef" }

  let planResult =
    getMigratePlanWithSchema oldDbPath schemaIdentity schema newDbPath
    |> fun t -> t.Result

  match planResult with
  | Error ex -> failwith $"Expected getMigratePlanWithSchema to succeed, got: {ex.Message}"
  | Ok report ->
    Assert.Equal("abcdef0123456789", report.schemaHash)
    Assert.Equal(Some "deadbeef", report.schemaCommit)
    Assert.True(report.canRunMigrate)
    Assert.Contains("student", report.plannedCopyTargets)
    Assert.Empty report.unsupportedDifferences

[<Fact>]
let ``dbTxn rejects requests while target database status is migrating`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_dbtxn_target_migrating_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "marketdesk-1111222233334444.sqlite")
  createStudentDatabase dbPath [ "Alice"; "Bob" ] (Some "migrating")

  let result =
    dbTxn dbPath {
      let! count =
        fun tx ->
          task {
            use cmd = new SqliteCommand("SELECT COUNT(*) FROM student", tx.Connection, tx)
            let! countObj = cmd.ExecuteScalarAsync()
            return Ok(Convert.ToInt64 countObj)
          }

      return count
    }
    |> fun t -> t.Result

  match result with
  | Ok count -> failwith $"Expected migrating target database to reject requests, got count: {count}"
  | Error ex -> Assert.Contains("Target database is still migrating", ex.Message)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``dbTxn allows requests when target database status is ready`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_dbtxn_target_ready_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "marketdesk-aaaabbbbccccdddd.sqlite")
  createStudentDatabase dbPath [ "Alice"; "Bob"; "Carol" ] (Some "ready")

  let result =
    dbTxn dbPath {
      let! count =
        fun tx ->
          task {
            use cmd = new SqliteCommand("SELECT COUNT(*) FROM student", tx.Connection, tx)
            let! countObj = cmd.ExecuteScalarAsync()
            return Ok(Convert.ToInt64 countObj)
          }

      return count
    }
    |> fun t -> t.Result

  match result with
  | Error ex -> failwith $"Expected ready target database to serve requests, got error: {ex.Message}"
  | Ok count -> Assert.Equal(3L, count)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``dbRuntime rejects requests while target database status is migrating`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_dbruntime_target_migrating_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "marketdesk-1111222233334444.sqlite")
  createStudentDatabase dbPath [ "Alice"; "Bob" ] (Some "migrating")
  let runtime = dbRuntime dbPath

  let result =
    runtime.RunInTransaction id (fun tx ->
      task {
        use cmd = new SqliteCommand("SELECT COUNT(*) FROM student", tx.Connection, tx)
        let! countObj = cmd.ExecuteScalarAsync()
        return Ok(Convert.ToInt64 countObj)
      })
    |> fun t -> t.Result

  match result with
  | Ok count -> failwith $"Expected migrating target database to reject dbRuntime requests, got count: {count}"
  | Error ex -> Assert.Contains("Target database is still migrating", ex.Message)

  Directory.Delete(tempDir, true)

[<AutoIncPK "id">]
[<Unique "name">]
[<Index "name">]
[<SelectBy("name", "age")>]
[<SelectLike "name">]
[<SelectByOrInsert("name", "age")>]
[<InsertOrIgnore>]
[<Upsert>]
type ReflectionStudent = { id: int64; name: string; age: int64 }

[<AutoIncPK "id">]
type ReflectionUser = { id: int64; name: string }

[<AutoIncPK "id">]
[<OnDeleteCascade "user">]
type ReflectionUserWallet =
  { id: int64
    user: ReflectionUser
    address: string }

[<PK("tenantId", "userId")>]
[<Upsert>]
type ReflectionCompositeUser =
  { tenantId: string
    userId: string
    name: string }

[<PK "tenantId">]
[<PK "sku">]
type ReflectionSplitCompositeItem =
  { tenantId: string
    sku: string
    name: string }

[<AutoIncPK "id">]
[<OnDeleteCascade "user">]
type ReflectionCompositeWallet =
  { id: int64
    user: ReflectionCompositeUser
    address: string }

[<AutoIncPK "id">]
type ExplicitFkUser = { id: int64; name: string }

[<AutoIncPK "id">]
[<FK("explicit_fk_user", "userId")>]
[<OnDeleteCascade "userId">]
type ExplicitFkOrder =
  { id: int64
    userId: int64
    amount: float }

[<AutoIncPK "id">]
[<FK("explicit_fk_user", "tenantId", "userId", RefColumns = [| "tenant_id"; "user_id" |])>]
type ExplicitFkCompositeOrder =
  { id: int64
    tenantId: string
    userId: string
    amount: float }

type ReflectionStudentOpt = WithEmail of ReflectionStudent * email: string

[<View>]
[<Join(typeof<ReflectionCompositeWallet>, typeof<ReflectionCompositeUser>)>]
type ReflectionCompositeWalletView =
  { userTenantId: string
    userUserId: string
    userName: string
    address: string }

[<ViewSql "SELECT id, name FROM reflection_student">]
[<SelectBy "name">]
type ReflectionStudentView = { id: int64; name: string }

type ReflectionStatus =
  | Active
  | InProgress

[<AutoIncPK "id">]
[<SelectBy "status">]
type ReflectionStatusStudent =
  { id: int64
    name: string
    status: ReflectionStatus }

[<ViewSql "SELECT id, status FROM reflection_status_student">]
[<SelectBy "status">]
type ReflectionStatusStudentView = { id: int64; status: ReflectionStatus }

type PayloadStatus = Pending of int64

[<AutoIncPK "id">]
type UnsupportedPayloadStatusStudent = { id: int64; status: PayloadStatus }

[<AutoIncPK "id">]
type JoinStudent = { id: int64; name: string; age: int64 }

[<AutoIncPK "id">]
type JoinCourse =
  { id: int64
    title: string
    student: JoinStudent }

type JoinCourseGrade = { course: JoinCourse; grade: float }

[<PK "slug">]
[<SelectByOrInsert "slug">]
type SlugArticle = { slug: string; title: string }

[<PK "code">]
[<SelectByOrInsert "name">]
type Product = { code: string; name: string }

type ProductOpt = WithStock of Product * stock: int64

[<PK("tenantId", "code")>]
type CompositeProduct =
  { tenantId: string
    code: string
    name: string }

type CompositeProductOpt = WithStock of CompositeProduct * stock: int64

[<AutoIncPK "id">]
[<PK "id">]
type ConflictingPkStudent = { id: int64; name: string }

type ExternalAccount = { id: int64; name: string }

[<AutoIncPK "id">]
type WalletWithOutsideRef = { id: int64; account: ExternalAccount }

type ParentWithoutPk = { name: string }

[<AutoIncPK "id">]
type ChildWithParentWithoutPk = { id: int64; parent: ParentWithoutPk }

[<AutoIncPK "id">]
type JoinChainA = { id: int64; name: string }

[<AutoIncPK "id">]
type JoinChainB = { id: int64; chainA: JoinChainA }

[<AutoIncPK "id">]
type JoinChainC = { id: int64; label: string }

[<AutoIncPK "id">]
type JoinChainD = { id: int64; chainC: JoinChainC }

[<View>]
[<Join(typeof<JoinChainA>, typeof<JoinChainB>)>]
[<Join(typeof<JoinChainC>, typeof<JoinChainD>)>]
type DisconnectedJoinView = { id: int64 }

[<View>]
[<Join(typeof<JoinCourse>, typeof<JoinStudent>)>]
[<Join(typeof<JoinCourseGrade>, typeof<JoinCourse>)>]
type JoinStudentCourseGrade =
  { studentId: int64
    studentName: string
    title: string
    grade: float }

[<Fact>]
let ``schema reflection maps records, foreign keys, and query annotations`` () =
  let types =
    [ typeof<ReflectionStudent>
      typeof<ReflectionUser>
      typeof<ReflectionUserWallet> ]

  match buildSchemaFromTypes types with
  | Error e -> failwith $"reflection failed: {e}"
  | Ok schema ->
    let student =
      schema.tables |> List.find (fun table -> table.name = "reflection_student")

    let idColumn = student.columns |> List.find (fun column -> column.name = "id")

    let hasAutoPk =
      idColumn.constraints
      |> List.exists (function
        | PrimaryKey pk -> pk.isAutoincrement
        | _ -> false)

    Assert.True hasAutoPk

    Assert.True(
      student.queryByAnnotations
      |> List.exists (fun q -> q.columns = [ "name"; "age" ])
    )

    Assert.True(student.queryLikeAnnotations |> List.exists (fun q -> q.columns = [ "name" ]))

    Assert.True(
      student.queryByOrCreateAnnotations
      |> List.exists (fun q -> q.columns = [ "name"; "age" ])
    )

    Assert.True(not student.insertOrIgnoreAnnotations.IsEmpty)
    Assert.True(not student.upsertAnnotations.IsEmpty)

    Assert.True(
      schema.indexes
      |> List.exists (fun index -> index.name = "ix_reflection_student_name")
    )

    let wallet =
      schema.tables |> List.find (fun table -> table.name = "reflection_user_wallet")

    let userFkColumn =
      wallet.columns |> List.find (fun column -> column.name = "user_id")

    let fkConstraint =
      userFkColumn.constraints
      |> List.tryPick (function
        | ForeignKey fk -> Some fk
        | _ -> None)

    match fkConstraint with
    | None -> failwith "Expected foreign key on reflection_user_wallet.user_id"
    | Some fk ->
      Assert.Equal("reflection_user", fk.refTable)
      Assert.Equal<string>("id", fk.refColumns.Head)

      match fk.onDelete with
      | Some Cascade -> ()
      | _ -> failwith "Expected ON DELETE CASCADE on reflection_user_wallet.user_id"

[<Fact>]
let ``schema reflection maps composite PKs and composite FKs`` () =
  let types = [ typeof<ReflectionCompositeUser>; typeof<ReflectionCompositeWallet> ]

  match buildSchemaFromTypes types with
  | Error error -> failwith $"Expected schema reflection to succeed, got: {error}"
  | Ok schema ->
    let user =
      schema.tables
      |> List.find (fun table -> table.name = "reflection_composite_user")

    let primaryKey =
      user.constraints
      |> List.tryPick (function
        | PrimaryKey pk -> Some pk
        | _ -> None)

    match primaryKey with
    | None -> failwith "Expected table-level primary key on reflection_composite_user"
    | Some pk -> Assert.Equal<string list>([ "tenant_id"; "user_id" ], pk.columns)

    let wallet =
      schema.tables
      |> List.find (fun table -> table.name = "reflection_composite_wallet")

    Assert.True(wallet.columns |> List.exists (fun column -> column.name = "user_tenant_id"))
    Assert.True(wallet.columns |> List.exists (fun column -> column.name = "user_user_id"))

    let foreignKey =
      wallet.constraints
      |> List.tryPick (function
        | ForeignKey fk -> Some fk
        | _ -> None)

    match foreignKey with
    | None -> failwith "Expected table-level foreign key on reflection_composite_wallet"
    | Some fk ->
      Assert.Equal("reflection_composite_user", fk.refTable)
      Assert.Equal<string list>([ "user_tenant_id"; "user_user_id" ], fk.columns)
      Assert.Equal<string list>([ "tenant_id"; "user_id" ], fk.refColumns)

      match fk.onDelete with
      | Some Cascade -> ()
      | _ -> failwith "Expected ON DELETE CASCADE on reflection_composite_wallet.user"

[<Fact>]
let ``schema reflection supports composite PK declared by repeated PK attributes`` () =
  match buildSchemaFromTypes [ typeof<ReflectionSplitCompositeItem> ] with
  | Error error -> failwith $"Expected schema reflection to succeed, got: {error}"
  | Ok schema ->
    let item =
      schema.tables
      |> List.find (fun table -> table.name = "reflection_split_composite_item")

    let primaryKey =
      item.constraints
      |> List.tryPick (function
        | PrimaryKey pk -> Some pk
        | _ -> None)

    match primaryKey with
    | None -> failwith "Expected table-level primary key on reflection_split_composite_item"
    | Some pk -> Assert.Equal<string list>([ "tenant_id"; "sku" ], pk.columns)

[<Fact>]
let ``schema reflection supports explicit FK attribute with single column`` () =
  let types = [ typeof<ExplicitFkUser>; typeof<ExplicitFkOrder> ]

  match buildSchemaFromTypes types with
  | Error error -> failwith $"Expected schema reflection to succeed, got: {error}"
  | Ok schema ->
    let order =
      schema.tables |> List.find (fun table -> table.name = "explicit_fk_order")

    let foreignKey =
      order.constraints
      |> List.tryPick (function
        | ForeignKey fk -> Some fk
        | _ -> None)

    match foreignKey with
    | None -> failwith "Expected foreign key on explicit_fk_order"
    | Some fk ->
      Assert.Equal("explicit_fk_user", fk.refTable)
      Assert.Equal<string list>([ "user_id" ], fk.columns)
      Assert.Equal<string list>([], fk.refColumns)

      match fk.onDelete with
      | Some Cascade -> ()
      | _ -> failwith "Expected ON DELETE CASCADE on explicit_fk_order.userId"

[<Fact>]
let ``schema reflection supports explicit FK attribute with multiple columns`` () =
  let types = [ typeof<ExplicitFkUser>; typeof<ExplicitFkCompositeOrder> ]

  match buildSchemaFromTypes types with
  | Error error -> failwith $"Expected schema reflection to succeed, got: {error}"
  | Ok schema ->
    let order =
      schema.tables
      |> List.find (fun table -> table.name = "explicit_fk_composite_order")

    let foreignKey =
      order.constraints
      |> List.tryPick (function
        | ForeignKey fk -> Some fk
        | _ -> None)

    match foreignKey with
    | None -> failwith "Expected foreign key on explicit_fk_composite_order"
    | Some fk ->
      Assert.Equal("explicit_fk_user", fk.refTable)
      Assert.Equal<string list>([ "tenant_id"; "user_id" ], fk.columns)
      Assert.Equal<string list>([ "tenant_id"; "user_id" ], fk.refColumns)

[<Fact>]
let ``schema diff detects renamed tables only when target declares PreviousName`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "legacy_student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ { mkTable
                "student"
                [ mkColumn
                    "id"
                    SqlInteger
                    [ PrimaryKey
                        { constraintName = None
                          columns = []
                          isAutoincrement = true } ]
                  mkColumn "name" SqlText [ NotNull ] ]
                [] with
                previousName = Some "legacy_student" } ] }

  let diff = diffSchemas sourceSchema targetSchema

  Assert.Empty diff.addedTables
  Assert.Empty diff.removedTables
  Assert.Equal<(string * string) list>([ "legacy_student", "student" ], diff.renamedTables)
  Assert.Equal<(string * string) list>([ "legacy_student", "student" ], diff.matchedTables)

[<Fact>]
let ``schema reflection preserves PreviousName metadata for tables and columns`` () =
  match buildSchemaFromTypes [ typeof<ReflectionStudentWithPreviousName> ] with
  | Error error -> failwith $"Expected reflection to succeed, got: {error}"
  | Ok schema ->
    let table =
      schema.tables
      |> List.find (fun item -> item.name = "reflection_student_with_previous_name")

    let nameColumn = table.columns |> List.find (fun column -> column.name = "name")

    Assert.Equal(Some "legacy_student", table.previousName)
    Assert.Equal(Some "full_name", nameColumn.previousName)

[<Fact>]
let ``schema reflection preserves DropColumn metadata for tables`` () =
  match buildSchemaFromTypes [ typeof<ReflectionStudentWithDroppedColumn> ] with
  | Error error -> failwith $"Expected schema reflection to succeed, got: {error}"
  | Ok schema ->
    let table =
      schema.tables
      |> List.find (fun item -> item.name = "reflection_student_with_dropped_column")

    Assert.Equal<string list>([ "legacy_note" ], table.dropColumns)

[<Fact>]
let ``schema diff treats table name changes without PreviousName as add and remove`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "legacy_student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ mkTable
              "student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              [] ] }

  let diff = diffSchemas sourceSchema targetSchema

  Assert.Equal<string list>([ "student" ], diff.addedTables)
  Assert.Equal<string list>([ "legacy_student" ], diff.removedTables)
  Assert.Empty diff.renamedTables

[<Fact>]
let ``table copy mapping uses explicit PreviousName for renamed columns and defaults for additions`` () =
  let sourceTable =
    mkTable
      "student"
      [ mkColumn
          "id"
          SqlInteger
          [ PrimaryKey
              { constraintName = None
                columns = []
                isAutoincrement = true } ]
        mkColumn "full_name" SqlText [ NotNull ]
        mkColumn "age" SqlInteger [ NotNull ]
        mkColumn "legacy_note" SqlText [ NotNull ] ]
      []

  let targetTable =
    mkTable
      "student"
      [ mkColumn
          "id"
          SqlInteger
          [ PrimaryKey
              { constraintName = None
                columns = []
                isAutoincrement = true } ]
        { mkColumn "name" SqlText [ NotNull ] with
            previousName = Some "full_name" }
        mkColumn "age" SqlInteger [ NotNull ]
        mkColumn "status" SqlText [ NotNull; Default(String "active") ]
        mkColumn "score" SqlReal [] ]
      []

  let mapping =
    match buildTableCopyMapping sourceTable targetTable with
    | Ok value -> value
    | Error error -> failwith $"Expected table copy mapping to succeed, got: {error}"

  let byTarget =
    mapping.columnMappings
    |> List.map (fun entry -> entry.targetColumn, entry.source)
    |> Map.ofList

  match byTarget["id"] with
  | SourceColumn "id" -> ()
  | other -> failwith $"Expected id to map from source id, got {other}"

  match byTarget["name"] with
  | SourceColumn "full_name" -> ()
  | other -> failwith $"Expected name to map from full_name, got {other}"

  match byTarget["status"] with
  | DefaultExpr(String "active") -> ()
  | other -> failwith $"Expected status default to be 'active', got {other}"

  match byTarget["score"] with
  | DefaultExpr(Value "NULL") -> ()
  | other -> failwith $"Expected score to use NULL for nullable addition, got {other}"

  Assert.Contains(("full_name", "name"), mapping.renamedColumns)
  Assert.Contains("status", mapping.addedTargetColumns)
  Assert.Contains("score", mapping.addedTargetColumns)
  Assert.Contains("legacy_note", mapping.droppedSourceColumns)

[<Fact>]
let ``table copy mapping fails for required added columns without defaults`` () =
  let sourceTable =
    mkTable
      "student"
      [ mkColumn
          "id"
          SqlInteger
          [ PrimaryKey
              { constraintName = None
                columns = []
                isAutoincrement = true } ]
        mkColumn "name" SqlText [ NotNull ] ]
      []

  let targetTable =
    mkTable
      "student"
      [ mkColumn
          "id"
          SqlInteger
          [ PrimaryKey
              { constraintName = None
                columns = []
                isAutoincrement = true } ]
        mkColumn "name" SqlText [ NotNull ]
        mkColumn "status" SqlText [ NotNull ] ]
      []

  match buildTableCopyMapping sourceTable targetTable with
  | Ok _ -> failwith "Expected table copy mapping to fail for a new required column without a default."
  | Error error ->
    Assert.Contains("student.status", error)
    Assert.Contains("new and required", error)

[<Fact>]
let ``table copy mapping allows explicitly dropped source columns`` () =
  let sourceTable =
    mkTable
      "student"
      [ mkColumn
          "id"
          SqlInteger
          [ PrimaryKey
              { constraintName = None
                columns = []
                isAutoincrement = true } ]
        mkColumn "name" SqlText [ NotNull ]
        mkColumn "legacy_note" SqlText [] ]
      []

  let targetTable =
    { mkTable
        "student"
        [ mkColumn
            "id"
            SqlInteger
            [ PrimaryKey
                { constraintName = None
                  columns = []
                  isAutoincrement = true } ]
          mkColumn "name" SqlText [ NotNull ] ]
        [] with
        dropColumns = [ "legacy_note" ] }

  let mapping =
    match buildTableCopyMapping sourceTable targetTable with
    | Ok value -> value
    | Error error -> failwith $"Expected table copy mapping to succeed, got: {error}"

  Assert.Empty mapping.droppedSourceColumns
  Assert.Equal<string list>([ "legacy_note" ], mapping.allowedDroppedSourceColumns)

[<Fact>]
let ``table copy mapping fails for incompatible same-name columns`` () =
  let sourceTable =
    mkTable
      "student"
      [ mkColumn
          "id"
          SqlInteger
          [ PrimaryKey
              { constraintName = None
                columns = []
                isAutoincrement = true } ]
        mkColumn "score" SqlReal [] ]
      []

  let targetTable =
    mkTable
      "student"
      [ mkColumn
          "id"
          SqlInteger
          [ PrimaryKey
              { constraintName = None
                columns = []
                isAutoincrement = true } ]
        mkColumn "score" SqlText [] ]
      []

  match buildTableCopyMapping sourceTable targetTable with
  | Ok _ -> failwith "Expected table copy mapping to fail for incompatible same-name columns."
  | Error error ->
    Assert.Contains("student.score", error)
    Assert.Contains("not copy-compatible", error)

[<Fact>]
let ``schema copy plan keeps explicit renamed table mappings`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "legacy_user"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "full_name" SqlText [ NotNull ] ]
              []
            mkTable
              "audit_log"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "message" SqlText [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ { mkTable
                "user"
                [ mkColumn
                    "id"
                    SqlInteger
                    [ PrimaryKey
                        { constraintName = None
                          columns = []
                          isAutoincrement = true } ]
                  { mkColumn "name" SqlText [ NotNull ] with
                      previousName = Some "full_name" }
                  mkColumn "status" SqlText [ NotNull; Default(String "active") ] ]
                [] with
                previousName = Some "legacy_user" }
            mkTable
              "audit_log"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "message" SqlText [ NotNull ] ]
              [] ] }

  let plan =
    match buildSchemaCopyPlan sourceSchema targetSchema with
    | Ok value -> value
    | Error error -> failwith $"Expected schema copy plan to succeed, got: {error}"

  Assert.Equal<(string * string) list>([ "legacy_user", "user" ], plan.diff.renamedTables)

  let userTableMapping =
    plan.tableMappings |> List.find (fun mapping -> mapping.targetTable = "user")

  Assert.Equal("legacy_user", userTableMapping.sourceTable)
  Assert.Contains(("full_name", "name"), userTableMapping.renamedColumns)

[<Fact>]
let ``schema copy plan fails when explicit PreviousName points to missing source column`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "full_name" SqlText [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ mkTable
              "student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                { mkColumn "name" SqlText [ NotNull ] with
                    previousName = Some "missing_name" } ]
              [] ] }

  match buildSchemaCopyPlan sourceSchema targetSchema with
  | Ok _ -> failwith "Expected schema copy plan to fail when PreviousName references a missing source column."
  | Error error ->
    Assert.Contains("PreviousName('missing_name')", error)
    Assert.Contains("source column 'student.missing_name' does not exist", error)

[<Fact>]
let ``schema copy plan fails when target schema drops a source column`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ]
                mkColumn "legacy_note" SqlText [] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ mkTable
              "student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              [] ] }

  match buildSchemaCopyPlan sourceSchema targetSchema with
  | Ok _ -> failwith "Expected schema copy plan to fail when a source column would be dropped."
  | Error error ->
    Assert.Contains("Dropping source columns is not supported", error)
    Assert.Contains("student: legacy_note", error)

[<Fact>]
let ``schema copy plan allows explicitly dropped source columns`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ]
                mkColumn "legacy_note" SqlText [] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ { mkTable
                "student"
                [ mkColumn
                    "id"
                    SqlInteger
                    [ PrimaryKey
                        { constraintName = None
                          columns = []
                          isAutoincrement = true } ]
                  mkColumn "name" SqlText [ NotNull ] ]
                [] with
                dropColumns = [ "legacy_note" ] } ] }

  let plan =
    match buildSchemaCopyPlan sourceSchema targetSchema with
    | Ok value -> value
    | Error error -> failwith $"Expected schema copy plan to succeed, got: {error}"

  let mapping =
    plan.tableMappings |> List.find (fun item -> item.targetTable = "student")

  Assert.Empty mapping.droppedSourceColumns
  Assert.Equal<string list>([ "legacy_note" ], mapping.allowedDroppedSourceColumns)

[<Fact>]
let ``schema copy plan fails when DropColumn references a missing source column`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ { mkTable
                "student"
                [ mkColumn
                    "id"
                    SqlInteger
                    [ PrimaryKey
                        { constraintName = None
                          columns = []
                          isAutoincrement = true } ]
                  mkColumn "name" SqlText [ NotNull ] ]
                [] with
                dropColumns = [ "legacy_note" ] } ] }

  match buildSchemaCopyPlan sourceSchema targetSchema with
  | Ok _ -> failwith "Expected schema copy plan to fail when DropColumn references a missing source column."
  | Error error ->
    Assert.Contains("declares DropColumn values that do not exist", error)
    Assert.Contains("legacy_note", error)

[<Fact>]
let ``schema copy plan fails when target schema removes a source table`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "audit_log"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "message" SqlText [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ mkTable
              "student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              [] ] }

  match buildSchemaCopyPlan sourceSchema targetSchema with
  | Ok _ -> failwith "Expected schema copy plan to fail when a source table would be removed."
  | Error error ->
    Assert.Contains("Removing source tables is not supported", error)
    Assert.Contains("audit_log", error)

[<Fact>]
let ``non-table consistency report passes for valid target schema objects`` () =
  let targetSchema =
    { emptyFile with
        tables =
          [ mkTable
              "student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              [] ]
        indexes =
          [ { name = "ix_student_name"
              table = "student"
              columns = [ "name" ] } ]
        views =
          [ { name = "student_view"
              previousName = None
              sql = "SELECT id, name FROM student"
              declaredColumns = []
              dependencies = [ "student" ]
              queryByAnnotations = []
              queryLikeAnnotations = []
              queryByOrCreateAnnotations = []
              selectOneAnnotations = []
              insertOrIgnoreAnnotations = []
              deleteAllAnnotations = []
              upsertAnnotations = [] } ]
        triggers =
          [ { name = "trg_student_insert"
              sql = "CREATE TRIGGER trg_student_insert AFTER INSERT ON student BEGIN SELECT 1; END;"
              dependencies = [ "student" ] } ] }

  let report = analyzeNonTableConsistency targetSchema

  Assert.Empty report.unsupportedLines
  Assert.Contains("non-table consistency checks: passed", report.supportedLines)

[<Fact>]
let ``non-table consistency report flags invalid target schema objects`` () =
  let targetSchema =
    { emptyFile with
        tables = [ mkTable "student" [ mkColumn "id" SqlInteger [ NotNull ] ] [] ]
        indexes =
          [ { name = "ix_student_ghost"
              table = "student"
              columns = [ "ghost" ] } ]
        views =
          [ { name = "student_view"
              previousName = None
              sql = "SELECT id FROM student"
              declaredColumns = []
              dependencies = [ "student"; "missing_table" ]
              queryByAnnotations = []
              queryLikeAnnotations = []
              queryByOrCreateAnnotations = []
              selectOneAnnotations = []
              insertOrIgnoreAnnotations = []
              deleteAllAnnotations = []
              upsertAnnotations = [] }
            { name = "student_view"
              previousName = None
              sql = "SELECT id FROM student"
              declaredColumns = []
              dependencies = [ "student" ]
              queryByAnnotations = []
              queryLikeAnnotations = []
              queryByOrCreateAnnotations = []
              selectOneAnnotations = []
              insertOrIgnoreAnnotations = []
              deleteAllAnnotations = []
              upsertAnnotations = [] } ]
        triggers =
          [ { name = "trg_student_insert"
              sql = ""
              dependencies = [ "missing_table" ] } ] }

  let report = analyzeNonTableConsistency targetSchema
  let unsupportedSummary = report.unsupportedLines |> String.concat "\n"

  Assert.NotEmpty report.unsupportedLines
  Assert.Contains("non-table consistency checks: found unsupported target-schema issues", report.supportedLines)
  Assert.Contains("Index 'ix_student_ghost' references missing columns", unsupportedSummary)
  Assert.Contains("Target view 'student_view' is declared 2 times.", unsupportedSummary)
  Assert.Contains("View 'student_view' references missing dependencies: missing_table.", unsupportedSummary)
  Assert.Contains("Trigger 'trg_student_insert' references missing dependencies: missing_table.", unsupportedSummary)
  Assert.Contains("Trigger 'trg_student_insert' has no SQL.", unsupportedSummary)

[<Fact>]
let ``dbTxn records writes into migration log when marker is recording`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_tasktxn_recording_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "recording.db")

  use setupConn = openSqliteConnection dbPath

  [ "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'recording');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupConn.Close()

  let result =
    dbTxn dbPath {
      let! newId =
        fun tx ->
          task {
            use cmd =
              new SqliteCommand("INSERT INTO student(name) VALUES (@name)", tx.Connection, tx)

            cmd.Parameters.AddWithValue("@name", "Alice") |> ignore
            MigrationLog.ensureWriteAllowed tx
            let! _ = cmd.ExecuteNonQueryAsync()
            use idCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
            let! idObj = idCmd.ExecuteScalarAsync()
            let id = idObj |> unbox<int64>
            MigrationLog.recordInsert tx "student" [ "id", box id; "name", box "Alice" ]
            return Ok id
          }

      return newId
    }
    |> fun t -> t.Result

  match result with
  | Error ex -> failwith $"Expected successful transaction, got error: {ex.Message}"
  | Ok id -> Assert.Equal(1L, id)

  use verifyConn = openSqliteConnection dbPath

  use logCmd =
    new SqliteCommand(
      "SELECT txn_id, ordering, operation, table_name, row_data FROM _migration_log ORDER BY id",
      verifyConn
    )

  use reader = logCmd.ExecuteReader()
  Assert.True(reader.Read())
  let txnId = reader.GetInt64(0)
  let ordering = reader.GetInt64(1)
  let operation = reader.GetString(2)
  let tableName = reader.GetString(3)
  let rowData = reader.GetString(4)

  Assert.True(txnId > 0L)
  Assert.Equal(1L, ordering)
  Assert.Equal("insert", operation)
  Assert.Equal("student", tableName)

  let json = JsonNode.Parse(rowData).AsObject()
  Assert.Equal("Alice", json["name"].GetValue<string>())
  Assert.Equal(1L, json["id"].GetValue<int64>())
  Assert.False(reader.Read())

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``dbTxn rejects writes when marker is draining`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_tasktxn_draining_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "draining.db")

  use setupConn = openSqliteConnection dbPath

  [ "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'draining');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupConn.Close()

  let result =
    dbTxn dbPath {
      let! _ =
        fun tx ->
          task {
            MigrationLog.ensureWriteAllowed tx

            use cmd =
              new SqliteCommand("INSERT INTO student(name) VALUES ('Bob')", tx.Connection, tx)

            let! _ = cmd.ExecuteNonQueryAsync()
            return Ok()
          }

      return ()
    }
    |> fun t -> t.Result

  match result with
  | Ok _ -> failwith "Expected draining mode to reject writes"
  | Error ex -> Assert.Contains("drain", ex.Message.ToLowerInvariant())

  use verifyConn = openSqliteConnection dbPath
  use countCmd = new SqliteCommand("SELECT COUNT(*) FROM student", verifyConn)
  let count = countCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(0L, count)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``dbTxn does not record writes when marker is absent`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_tasktxn_nomarker_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "nomarker.db")

  use setupConn = openSqliteConnection dbPath

  [ "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupConn.Close()

  let result =
    dbTxn dbPath {
      let! _ =
        fun tx ->
          task {
            use cmd =
              new SqliteCommand("INSERT INTO student(name) VALUES ('Carol')", tx.Connection, tx)

            MigrationLog.ensureWriteAllowed tx
            let! _ = cmd.ExecuteNonQueryAsync()
            MigrationLog.recordInsert tx "student" [ "name", box "Carol" ]
            return Ok()
          }

      return ()
    }
    |> fun t -> t.Result

  match result with
  | Error ex -> failwith $"Expected success without marker, got {ex.Message}"
  | Ok() -> ()

  use verifyConn = openSqliteConnection dbPath
  use countCmd = new SqliteCommand("SELECT COUNT(*) FROM _migration_log", verifyConn)
  let count = countCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(0L, count)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``dbTxn binds plain task values without transaction parameter`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_tasktxn_bind_task_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "bind-task.db")

  let result =
    dbTxn dbPath {
      let! prefix = Task.FromResult "Al"
      let! suffix = Task.FromResult "ice"
      return $"{prefix}{suffix}"
    }
    |> fun t -> t.Result

  match result with
  | Error ex -> failwith $"Expected successful bind for Task<_>, got error: {ex.Message}"
  | Ok value -> Assert.Equal("Alice", value)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``dbTxn binds Task Result with sqlite exception and unwraps Ok`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_tasktxn_bind_task_result_ok_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "bind-task-result-ok.db")

  let result =
    dbTxn dbPath {
      let! value = Task.FromResult(Ok 41L: Result<int64, SqliteException>)

      return value + 1L
    }
    |> fun t -> t.Result

  match result with
  | Error ex -> failwith $"Expected successful bind for Task<Result<_, SqliteException>>, got error: {ex.Message}"
  | Ok value -> Assert.Equal(42L, value)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``dbTxn short-circuits Task Result sqlite exception errors`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_tasktxn_bind_task_result_error_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "bind-task-result-error.db")
  let mutable continuationRan = false

  let result =
    dbTxn dbPath {
      let! (_: int64) = Task.FromResult(Error(SqliteException("bind failure", 0)): Result<int64, SqliteException>)

      continuationRan <- true
      return 1L
    }
    |> fun t -> t.Result

  match result with
  | Ok value -> failwith $"Expected Error from Task<Result<_, SqliteException>> bind, got Ok {value}"
  | Error ex ->
    Assert.Contains("bind failure", ex.Message)
    Assert.False continuationRan

  Directory.Delete(tempDir, true)

[<Fact>]
let ``dbTxn binds generic Task Result values and unwraps Ok`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_tasktxn_bind_task_result_generic_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "bind-task-result-generic.db")

  let result =
    dbTxn dbPath {
      let! value = Task.FromResult(Ok 13L: Result<int64, string>)
      return value + 1L
    }
    |> fun t -> t.Result

  match result with
  | Error ex -> failwith $"Expected successful bind for Task<Result<_, _>>, got error: {ex.Message}"
  | Ok value -> Assert.Equal(14L, value)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``dbTxn short-circuits generic Task Result errors by mapping to sqlite exception`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_tasktxn_bind_task_result_generic_error_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "bind-task-result-generic-error.db")
  let mutable continuationRan = false

  let result =
    dbTxn dbPath {
      let! (_: int64) = Task.FromResult(Error "external-error": Result<int64, string>)

      continuationRan <- true
      return 1L
    }
    |> fun t -> t.Result

  match result with
  | Ok value -> failwith $"Expected Error from Task<Result<_, _>> bind, got Ok {value}"
  | Error ex ->
    Assert.Contains("external-error", ex.Message)
    Assert.False continuationRan

  Directory.Delete(tempDir, true)

[<Fact>]
let ``taskResult unwraps Task Result sqlite values`` () =
  let result =
    taskResult {
      let! prefix = Task.FromResult "Al"
      let! (suffix: string) = Task.FromResult(Ok "ice": Result<string, SqliteException>)
      return $"{prefix}{suffix}"
    }
    |> fun t -> t.Result

  match result with
  | Error ex -> failwith $"Expected successful taskResult bind, got error: {ex.Message}"
  | Ok value -> Assert.Equal("Alice", value)

[<Fact>]
let ``taskResult short-circuits Result sqlite errors`` () =
  let mutable continuationRan = false

  let result =
    taskResult {
      let! (_: int) = Error(SqliteException("taskResult failure", 0))
      continuationRan <- true
      return 1
    }
    |> fun t -> t.Result

  match result with
  | Ok value -> failwith $"Expected Error from taskResult, got Ok {value}"
  | Error ex ->
    Assert.Contains("taskResult failure", ex.Message)
    Assert.False continuationRan

[<Fact>]
let ``dbRuntime rolls back transactions on generic errors without remapping them`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_dbruntime_generic_error_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "dbruntime-generic-error.db")

  use setupConn = openSqliteConnection dbPath

  use createCmd =
    new SqliteCommand("CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);", setupConn)

  createCmd.ExecuteNonQuery() |> ignore
  setupConn.Close()
  let runtime = dbRuntime dbPath

  let result: Result<unit, string> =
    runtime.RunInTransaction (fun ex -> ex.Message) (fun tx ->
      task {
        use cmd =
          new SqliteCommand("INSERT INTO student(name) VALUES ('Alice')", tx.Connection, tx)

        let! _ = cmd.ExecuteNonQueryAsync()
        return Error "app-error"
      })
    |> fun t -> t.Result

  match result with
  | Ok() -> failwith "Expected generic error from DbRuntime.RunInTransaction"
  | Error error -> Assert.Equal("app-error", error)

  use verifyConn = openSqliteConnection dbPath
  use countCmd = new SqliteCommand("SELECT COUNT(*) FROM student", verifyConn)
  let count = countCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(0L, count)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``runSimple commits transaction and applies queued response effects`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_web_run_simple_success_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "web-success.db")

  use setupConn = openSqliteConnection dbPath

  use createCmd =
    new SqliteCommand(
      "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, created_at TEXT NOT NULL);",
      setupConn
    )

  createCmd.ExecuteNonQuery() |> ignore
  setupConn.Close()

  let env = createTestWebEnv dbPath
  let httpContext = createTestHttpContext ()
  let expectedNow = (env :> IClock).UtcNowRfc3339()

  let operation =
    webResult {
      let! now = Clock.utcNowRfc3339

      let! _ =
        fun (tx: SqliteTransaction) ->
          task {
            use cmd =
              new SqliteCommand("INSERT INTO student(created_at) VALUES (@created_at)", tx.Connection, tx)

            cmd.Parameters.AddWithValue("@created_at", now) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return Ok()
          }

      do! Respond.statusCode 201
      do! Respond.text now
      return now
    }

  let result = runSimple env (Some httpContext) operation |> fun t -> t.Result

  match result with
  | Error error -> failwith $"Expected successful web run, got error: {error}"
  | Ok now -> Assert.Equal(expectedNow, now)

  Assert.Equal(201, httpContext.Response.StatusCode)
  Assert.Equal("text/plain; charset=utf-8", httpContext.Response.ContentType)

  httpContext.Response.Body.Position <- 0L

  use reader =
    new StreamReader(httpContext.Response.Body, Encoding.UTF8, false, 1024, true)

  let responseBody = reader.ReadToEnd()
  Assert.Equal(expectedNow, responseBody)

  use verifyConn = openSqliteConnection dbPath
  use countCmd = new SqliteCommand("SELECT COUNT(*) FROM student", verifyConn)
  let count = countCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, count)

  use valueCmd =
    new SqliteCommand("SELECT created_at FROM student LIMIT 1", verifyConn)

  let storedCreatedAt = valueCmd.ExecuteScalar() |> string
  Assert.Equal(expectedNow, storedCreatedAt)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``runSimple rolls back transaction and skips response effects on app error`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_web_run_simple_error_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "web-error.db")

  use setupConn = openSqliteConnection dbPath

  use createCmd =
    new SqliteCommand("CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);", setupConn)

  createCmd.ExecuteNonQuery() |> ignore
  setupConn.Close()

  let env = createTestWebEnv dbPath
  let httpContext = createTestHttpContext ()

  let operation =
    webResult {
      let! _ =
        fun (tx: SqliteTransaction) ->
          task {
            use cmd =
              new SqliteCommand("INSERT INTO student(name) VALUES ('Alice')", tx.Connection, tx)

            let! _ = cmd.ExecuteNonQueryAsync()
            return Ok()
          }

      do! Respond.statusCode 201
      do! Respond.text "should-not-be-written"
      return! Web.fail "boom"
    }

  let result = runSimple env (Some httpContext) operation |> fun t -> t.Result

  match result with
  | Ok() -> failwith "Expected app error from web operation"
  | Error(AppError error) -> Assert.Equal("boom", error)
  | Error other -> failwith $"Expected app error, got: {other}"

  Assert.Equal(200, httpContext.Response.StatusCode)
  httpContext.Response.Body.Position <- 0L

  use reader =
    new StreamReader(httpContext.Response.Body, Encoding.UTF8, false, 1024, true)

  let responseBody = reader.ReadToEnd()
  Assert.Equal("", responseBody)

  use verifyConn = openSqliteConnection dbPath
  use countCmd = new SqliteCommand("SELECT COUNT(*) FROM student", verifyConn)
  let count = countCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(0L, count)

  verifyConn.Close()
  Directory.Delete(tempDir, true)


[<Fact>]
let ``webResult binds TxnStep<Result<_, _>> as app errors`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_web_txn_app_result_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "web-txn-app-result.db")

  use setupConn = openSqliteConnection dbPath

  use createCmd =
    new SqliteCommand(
      "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE);",
      setupConn
    )

  createCmd.ExecuteNonQuery() |> ignore
  setupConn.Close()

  let env = createTestWebEnv dbPath
  let httpContext = createTestHttpContext ()

  let insertStudent (name: string) : TxnStep<Result<int64, string>> =
    txn {
      let! existingId =
        fun (tx: SqliteTransaction) ->
          task {
            use cmd =
              new SqliteCommand("SELECT id FROM student WHERE name = @name LIMIT 1", tx.Connection, tx)

            cmd.Parameters.AddWithValue("@name", name) |> ignore
            use! reader = cmd.ExecuteReaderAsync()
            let! hasRow = reader.ReadAsync()

            if hasRow then
              return Ok(Some(reader.GetInt64 0))
            else
              return Ok None
          }

      match existingId with
      | Some _ -> return Error "duplicate-student"
      | None ->
        let! id =
          fun (tx: SqliteTransaction) ->
            task {
              use cmd =
                new SqliteCommand("INSERT INTO student(name) VALUES (@name)", tx.Connection, tx)

              cmd.Parameters.AddWithValue("@name", name) |> ignore
              let! _ = cmd.ExecuteNonQueryAsync()
              use idCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
              let! idObj = idCmd.ExecuteScalarAsync()
              return Ok(idObj |> unbox<int64>)
            }

        return Ok id
    }

  let successOperation: WebOp<TestWebEnv, string, unit, int64> =
    webResult {
      let! id = (insertStudent "Alice" |> Web.ofTxnAppResult: WebOp<TestWebEnv, string, unit, int64>)

      do! (Respond.statusCode 201: WebOp<TestWebEnv, string, unit, unit>)
      do! (Respond.json {| id = id |}: WebOp<TestWebEnv, string, unit, unit>)
      return id
    }

  let successResult =
    runSimple env (Some httpContext) successOperation |> fun t -> t.Result

  match successResult with
  | Error error -> failwith $"Expected success from TxnStep<Result<_, _>> bind, got: {error}"
  | Ok id -> Assert.Equal(1L, id)

  Assert.Equal(201, httpContext.Response.StatusCode)
  Assert.Equal("application/json; charset=utf-8", httpContext.Response.ContentType)
  httpContext.Response.Body.Position <- 0L

  use reader =
    new StreamReader(httpContext.Response.Body, Encoding.UTF8, false, 1024, true)

  let responseBody = reader.ReadToEnd()
  Assert.Equal("""{"id":1}""", responseBody)

  let duplicateContext = createTestHttpContext ()

  let duplicateOperation: WebOp<TestWebEnv, string, unit, unit> =
    webResult {
      let! _ = (insertStudent "Alice" |> Web.ofTxnAppResult: WebOp<TestWebEnv, string, unit, int64>)

      do! (Respond.statusCode 201: WebOp<TestWebEnv, string, unit, unit>)
      do! (Respond.text "should-not-be-written": WebOp<TestWebEnv, string, unit, unit>)
      return ()
    }

  let duplicateResult =
    runSimple env (Some duplicateContext) duplicateOperation |> fun t -> t.Result

  match duplicateResult with
  | Ok() -> failwith "Expected duplicate app error from TxnStep<Result<_, _>> bind"
  | Error(AppError error) -> Assert.Equal("duplicate-student", error)
  | Error other -> failwith $"Expected app error, got: {other}"

  Assert.Equal(200, duplicateContext.Response.StatusCode)
  duplicateContext.Response.Body.Position <- 0L

  use duplicateReader =
    new StreamReader(duplicateContext.Response.Body, Encoding.UTF8, false, 1024, true)

  let duplicateResponseBody = duplicateReader.ReadToEnd()
  Assert.Equal("", duplicateResponseBody)

  use verifyConn = openSqliteConnection dbPath
  use countCmd = new SqliteCommand("SELECT COUNT(*) FROM student", verifyConn)
  let count = countCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, count)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``runSimple applies jsonWithTypeInfo responses`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_web_json_typeinfo_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "web-json-typeinfo.db")
  let env = createTestWebEnv dbPath
  let httpContext = createTestHttpContext ()

  let jsonTypeInfo =
    let options = JsonSerializerOptions(JsonSerializerDefaults.Web)
    options.TypeInfoResolver <- DefaultJsonTypeInfoResolver()
    options.GetTypeInfo(typeof<TestJsonResponse>) :?> JsonTypeInfo<TestJsonResponse>

  let operation: WebOp<TestWebEnv, string, unit, unit> =
    webResult {
      do! (Respond.jsonWithTypeInfo jsonTypeInfo (TestJsonResponse(Id = 42L)): WebOp<TestWebEnv, string, unit, unit>)

      return ()
    }

  let result = runSimple env (Some httpContext) operation |> fun t -> t.Result

  match result with
  | Error error -> failwith $"Expected success from jsonWithTypeInfo response, got: {error}"
  | Ok() -> ()

  Assert.Equal("application/json; charset=utf-8", httpContext.Response.ContentType)
  httpContext.Response.Body.Position <- 0L

  use reader =
    new StreamReader(httpContext.Response.Body, Encoding.UTF8, false, 1024, true)

  let responseBody = reader.ReadToEnd()
  Assert.Equal("""{"id":42}""", responseBody)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``runSimple returns MissingHttpContext and rolls back queued response operations`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_web_run_simple_missing_http_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "web-missing-http.db")

  use setupConn = openSqliteConnection dbPath

  use createCmd =
    new SqliteCommand("CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);", setupConn)

  createCmd.ExecuteNonQuery() |> ignore
  setupConn.Close()

  let env = createTestWebEnv dbPath

  let operation =
    webResult {
      let! _ =
        fun (tx: SqliteTransaction) ->
          task {
            use cmd =
              new SqliteCommand("INSERT INTO student(name) VALUES ('Alice')", tx.Connection, tx)

            let! _ = cmd.ExecuteNonQueryAsync()
            return Ok()
          }

      do! Respond.text "missing-http"
      return ()
    }

  let result = runSimple env None operation |> fun t -> t.Result

  match result with
  | Ok() -> failwith "Expected MissingHttpContext error"
  | Error MissingHttpContext -> ()
  | Error other -> failwith $"Expected MissingHttpContext, got: {other}"

  use verifyConn = openSqliteConnection dbPath
  use countCmd = new SqliteCommand("SELECT COUNT(*) FROM student", verifyConn)
  let count = countCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(0L, count)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``txn composes reusable transaction-scoped operations inside dbTxn`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_txn_compose_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "compose.db")

  use setupConn = openSqliteConnection dbPath

  use createCmd =
    new SqliteCommand("CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);", setupConn)

  createCmd.ExecuteNonQuery() |> ignore
  setupConn.Close()

  let insertStudent (name: string) =
    txn {
      let! id =
        fun tx ->
          task {
            use cmd =
              new SqliteCommand("INSERT INTO student(name) VALUES (@name)", tx.Connection, tx)

            cmd.Parameters.AddWithValue("@name", name) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            use idCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
            let! idObj = idCmd.ExecuteScalarAsync()
            return Ok(idObj |> unbox<int64>)
          }

      return id
    }

  let result =
    dbTxn dbPath {
      let! firstId = insertStudent "Alice"
      let! secondId = insertStudent "Bob"
      return firstId, secondId
    }
    |> fun t -> t.Result

  match result with
  | Error ex -> failwith $"Expected successful composition using txn, got error: {ex.Message}"
  | Ok(firstId, secondId) ->
    Assert.Equal(1L, firstId)
    Assert.Equal(2L, secondId)

  use verifyConn = openSqliteConnection dbPath
  use countCmd = new SqliteCommand("SELECT COUNT(*) FROM student", verifyConn)
  let count = countCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(2L, count)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``bulk copy plan orders parent tables before FK dependents`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "legacy_account"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "legacy_account_id" SqlInteger [ NotNull; mkForeignKey "legacy_account" [ "id" ] ]
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ { mkTable
                "account"
                [ mkColumn
                    "id"
                    SqlInteger
                    [ PrimaryKey
                        { constraintName = None
                          columns = []
                          isAutoincrement = true } ]
                  mkColumn "name" SqlText [ NotNull ] ]
                [] with
                previousName = Some "legacy_account" }
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                { mkColumn "account_id" SqlInteger [ NotNull; mkForeignKey "account" [ "id" ] ] with
                    previousName = Some "legacy_account_id" }
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  match buildBulkCopyPlan sourceSchema targetSchema with
  | Error error -> failwith $"bulk copy plan failed: {error}"
  | Ok plan ->
    let orderedTargets = plan.steps |> List.map (fun step -> step.mapping.targetTable)

    Assert.Equal<string list>([ "account"; "invoice" ], orderedTargets)

    let accountStep =
      plan.steps |> List.find (fun step -> step.mapping.targetTable = "account")

    let invoiceStep =
      plan.steps |> List.find (fun step -> step.mapping.targetTable = "invoice")

    Assert.Equal<string list>([ "name" ], accountStep.insertColumns)
    Assert.Equal<string list>([ "account_id"; "total" ], invoiceStep.insertColumns)

[<Fact>]
let ``bulk copy row projection translates FK values via ID mapping`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "legacy_account"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "legacy_account_id" SqlInteger [ NotNull; mkForeignKey "legacy_account" [ "id" ] ]
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ { mkTable
                "account"
                [ mkColumn
                    "id"
                    SqlInteger
                    [ PrimaryKey
                        { constraintName = None
                          columns = []
                          isAutoincrement = true } ]
                  mkColumn "name" SqlText [ NotNull ] ]
                [] with
                previousName = Some "legacy_account" }
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                { mkColumn "account_id" SqlInteger [ NotNull; mkForeignKey "account" [ "id" ] ] with
                    previousName = Some "legacy_account_id" }
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  match buildBulkCopyPlan sourceSchema targetSchema with
  | Error error -> failwith $"bulk copy plan failed: {error}"
  | Ok plan ->
    let accountStep =
      plan.steps |> List.find (fun step -> step.mapping.targetTable = "account")

    let invoiceStep =
      plan.steps |> List.find (fun step -> step.mapping.targetTable = "invoice")

    let sourceAccountRow = mkRow [ "id", Integer 10; "name", String "Alice" ]

    let sourceInvoiceRow =
      mkRow [ "id", Integer 1; "legacy_account_id", Integer 10; "total", Real 42.5 ]

    let accountTargetRow, _, accountInsertValues =
      match projectRowForInsert accountStep sourceAccountRow emptyIdMappings with
      | Ok result -> result
      | Error error -> failwith $"account projection failed: {error}"

    Assert.Equal<Expr list>([ String "Alice" ], accountInsertValues)

    let idMappingsAfterAccount =
      match recordIdMapping accountStep sourceAccountRow accountTargetRow (Some [ Integer 100 ]) emptyIdMappings with
      | Ok mappings -> mappings
      | Error error -> failwith $"record ID mapping failed: {error}"

    let invoiceTargetRow, _, invoiceInsertValues =
      match projectRowForInsert invoiceStep sourceInvoiceRow idMappingsAfterAccount with
      | Ok result -> result
      | Error error -> failwith $"invoice projection failed: {error}"

    Assert.Equal<Expr list>([ Integer 100; Real 42.5 ], invoiceInsertValues)

    match invoiceTargetRow.TryFind "account_id" with
    | Some(Integer 100) -> ()
    | other -> failwith $"Expected translated account_id=100, got {other}"

[<Fact>]
let ``bulk copy row projection fails when FK mapping is missing`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "legacy_account"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "legacy_account_id" SqlInteger [ NotNull; mkForeignKey "legacy_account" [ "id" ] ]
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ { mkTable
                "account"
                [ mkColumn
                    "id"
                    SqlInteger
                    [ PrimaryKey
                        { constraintName = None
                          columns = []
                          isAutoincrement = true } ]
                  mkColumn "name" SqlText [ NotNull ] ]
                [] with
                previousName = Some "legacy_account" }
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                { mkColumn "account_id" SqlInteger [ NotNull; mkForeignKey "account" [ "id" ] ] with
                    previousName = Some "legacy_account_id" }
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  match buildBulkCopyPlan sourceSchema targetSchema with
  | Error error -> failwith $"bulk copy plan failed: {error}"
  | Ok plan ->
    let invoiceStep =
      plan.steps |> List.find (fun step -> step.mapping.targetTable = "invoice")

    let sourceInvoiceRow =
      mkRow [ "id", Integer 1; "legacy_account_id", Integer 10; "total", Real 42.5 ]

    match projectRowForInsert invoiceStep sourceInvoiceRow emptyIdMappings with
    | Ok _ -> failwith "Expected projection to fail when account ID mapping is missing"
    | Error error -> Assert.Contains("No ID mappings are available yet for referenced table 'account'", error)

[<Fact>]
let ``drain replay groups entries by transaction and ordering`` () =
  let entries =
    [ mkLogEntry
        4L
        2L
        2L
        Insert
        "invoice"
        (mkRow [ "id", Integer 60; "legacy_account_id", Integer 10; "total", Real 42.5 ])
      mkLogEntry 2L 1L 2L Update "invoice" (mkRow [ "id", Integer 50; "total", Real 99.0 ])
      mkLogEntry 3L 2L 1L Insert "account" (mkRow [ "id", Integer 10; "name", String "Alice" ])
      mkLogEntry 1L 1L 1L Insert "account" (mkRow [ "id", Integer 9; "name", String "Bob" ]) ]

  let grouped = groupEntriesByTransaction entries

  Assert.Equal<int64 list>([ 1L; 2L ], grouped |> List.map fst)

  let txn1 =
    grouped |> List.find (fun (txnId, _) -> txnId = 1L) |> snd |> List.map _.id

  let txn2 =
    grouped |> List.find (fun (txnId, _) -> txnId = 2L) |> snd |> List.map _.id

  Assert.Equal<int64 list>([ 1L; 2L ], txn1)
  Assert.Equal<int64 list>([ 3L; 4L ], txn2)

[<Fact>]
let ``drain replay reads migration log entries and existing id mappings`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_drainreplay_read_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore
  let dbPath = Path.Combine(tempDir, "read.db")

  use setupConn = openSqliteConnection dbPath

  [ "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);"
    "CREATE TABLE _id_mapping(table_name TEXT NOT NULL, old_id INTEGER NOT NULL, new_id INTEGER NOT NULL, PRIMARY KEY(table_name, old_id));"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (7, 1, 'insert', 'legacy_account', '{\"id\":10,\"name\":\"Alice\"}');"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (7, 2, 'insert', 'invoice', '{\"id\":50,\"legacy_account_id\":10,\"total\":42.5}');"
    "INSERT INTO _id_mapping(table_name, old_id, new_id) VALUES ('account', 10, 100);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupConn)
    cmd.ExecuteNonQuery() |> ignore)

  let entriesResult = readMigrationLogEntries setupConn 0L |> fun t -> t.Result

  match entriesResult with
  | Error ex -> failwith $"Expected migration log read to succeed, got {ex.Message}"
  | Ok entries ->
    Assert.Equal(2, entries.Length)
    Assert.Equal(7L, entries.Head.txnId)
    Assert.Equal(Insert, entries.Head.operation)

    match entries.Head.rowData.TryFind "name" with
    | Some(String name) -> Assert.Equal("Alice", name)
    | other -> failwith $"Expected row_data name='Alice', got {other}"

    match entries.Tail.Head.rowData.TryFind "total" with
    | Some(Real total) -> Assert.True(Math.Abs(total - 42.5) < 0.0001)
    | other -> failwith $"Expected row_data total=42.5, got {other}"

  let mappingsResult = loadIdMappings setupConn |> fun t -> t.Result

  match mappingsResult with
  | Error ex -> failwith $"Expected ID mapping load to succeed, got {ex.Message}"
  | Ok mappings ->
    match lookupMappedIdentity "account" [ Integer 10 ] mappings with
    | Ok [ Integer mapped ] -> Assert.Equal(100, mapped)
    | Ok other -> failwith $"Expected mapped identity [Integer 100], got {other}"
    | Error error -> failwith $"Expected account mapping to exist, got error: {error}"

  setupConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``drain replay replays insert update and delete with id translation`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "legacy_account"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "legacy_account_id" SqlInteger [ NotNull; mkForeignKey "legacy_account" [ "id" ] ]
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ { mkTable
                "account"
                [ mkColumn
                    "id"
                    SqlInteger
                    [ PrimaryKey
                        { constraintName = None
                          columns = []
                          isAutoincrement = true } ]
                  mkColumn "name" SqlText [ NotNull ] ]
                [] with
                previousName = Some "legacy_account" }
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                { mkColumn "account_id" SqlInteger [ NotNull; mkForeignKey "account" [ "id" ] ] with
                    previousName = Some "legacy_account_id" }
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  match buildBulkCopyPlan sourceSchema targetSchema with
  | Error error -> failwith $"bulk copy plan failed: {error}"
  | Ok plan ->
    let tempDir =
      Path.Combine(Path.GetTempPath(), $"mig_drainreplay_apply_{Guid.NewGuid()}")

    Directory.CreateDirectory tempDir |> ignore
    let dbPath = Path.Combine(tempDir, "apply.db")

    use conn = openSqliteConnection dbPath

    [ "PRAGMA foreign_keys = ON;"
      "CREATE TABLE account(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
      "CREATE TABLE invoice(id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL, total REAL NOT NULL, FOREIGN KEY(account_id) REFERENCES account(id));"
      "CREATE TABLE _id_mapping(table_name TEXT NOT NULL, old_id INTEGER NOT NULL, new_id INTEGER NOT NULL, PRIMARY KEY(table_name, old_id));" ]
    |> List.iter (fun sql ->
      use cmd = new SqliteCommand(sql, conn)
      cmd.ExecuteNonQuery() |> ignore)

    let insertAndUpdateEntries =
      [ mkLogEntry 1L 11L 1L Insert "legacy_account" (mkRow [ "id", Integer 10; "name", String "Alice" ])
        mkLogEntry
          2L
          11L
          2L
          Insert
          "invoice"
          (mkRow [ "id", Integer 50; "legacy_account_id", Integer 10; "total", Real 42.5 ])
        mkLogEntry
          3L
          12L
          1L
          Update
          "invoice"
          (mkRow [ "id", Integer 50; "legacy_account_id", Integer 10; "total", Real 99.0 ]) ]

    let replayResult =
      replayDrainEntries conn plan insertAndUpdateEntries emptyIdMappings
      |> fun t -> t.Result

    let mappings =
      match replayResult with
      | Error ex -> failwith $"Expected replay to succeed, got {ex.Message}"
      | Ok value -> value

    use accountCmd = new SqliteCommand("SELECT id, name FROM account ORDER BY id", conn)
    use accountReader = accountCmd.ExecuteReader()
    Assert.True(accountReader.Read())
    let accountId = accountReader.GetInt64(0)
    Assert.Equal("Alice", accountReader.GetString(1))
    Assert.False(accountReader.Read())

    use invoiceCmd =
      new SqliteCommand("SELECT id, account_id, total FROM invoice ORDER BY id", conn)

    use invoiceReader = invoiceCmd.ExecuteReader()
    Assert.True(invoiceReader.Read())
    let invoiceId = invoiceReader.GetInt64(0)
    Assert.Equal(accountId, invoiceReader.GetInt64(1))
    Assert.True(Math.Abs(invoiceReader.GetDouble(2) - 99.0) < 0.0001)
    Assert.False(invoiceReader.Read())

    use accountMapCmd =
      new SqliteCommand("SELECT new_id FROM _id_mapping WHERE table_name = 'account' AND old_id = 10", conn)

    let mappedAccountId = accountMapCmd.ExecuteScalar() |> unbox<int64>
    Assert.Equal(accountId, mappedAccountId)

    use invoiceMapCmd =
      new SqliteCommand("SELECT new_id FROM _id_mapping WHERE table_name = 'invoice' AND old_id = 50", conn)

    let mappedInvoiceId = invoiceMapCmd.ExecuteScalar() |> unbox<int64>
    Assert.Equal(invoiceId, mappedInvoiceId)

    match lookupMappedIdentity "account" [ Integer 10 ] mappings with
    | Ok [ Integer mapped ] -> Assert.Equal(accountId, int64 mapped)
    | Ok other -> failwith $"Expected mapped account identity, got {other}"
    | Error error -> failwith $"Expected mapped account identity, got error: {error}"

    let deleteResult =
      replayDrainEntries conn plan [ mkLogEntry 4L 13L 1L Delete "invoice" (mkRow [ "id", Integer 50 ]) ] mappings
      |> fun t -> t.Result

    match deleteResult with
    | Error ex -> failwith $"Expected delete replay to succeed, got {ex.Message}"
    | Ok _ -> ()

    use countInvoiceCmd = new SqliteCommand("SELECT COUNT(*) FROM invoice", conn)
    let invoiceCount = countInvoiceCmd.ExecuteScalar() |> unbox<int64>
    Assert.Equal(0L, invoiceCount)

    conn.Close()
    Directory.Delete(tempDir, true)

[<Fact>]
let ``drain replay rolls back a transaction group when one operation fails`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "legacy_account"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "legacy_account_id" SqlInteger [ NotNull; mkForeignKey "legacy_account" [ "id" ] ]
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ { mkTable
                "account"
                [ mkColumn
                    "id"
                    SqlInteger
                    [ PrimaryKey
                        { constraintName = None
                          columns = []
                          isAutoincrement = true } ]
                  mkColumn "name" SqlText [ NotNull ] ]
                [] with
                previousName = Some "legacy_account" }
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                { mkColumn "account_id" SqlInteger [ NotNull; mkForeignKey "account" [ "id" ] ] with
                    previousName = Some "legacy_account_id" }
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  match buildBulkCopyPlan sourceSchema targetSchema with
  | Error error -> failwith $"bulk copy plan failed: {error}"
  | Ok plan ->
    let tempDir =
      Path.Combine(Path.GetTempPath(), $"mig_drainreplay_rollback_{Guid.NewGuid()}")

    Directory.CreateDirectory tempDir |> ignore
    let dbPath = Path.Combine(tempDir, "rollback.db")

    use conn = openSqliteConnection dbPath

    [ "PRAGMA foreign_keys = ON;"
      "CREATE TABLE account(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
      "CREATE TABLE invoice(id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL, total REAL NOT NULL, FOREIGN KEY(account_id) REFERENCES account(id));"
      "CREATE TABLE _id_mapping(table_name TEXT NOT NULL, old_id INTEGER NOT NULL, new_id INTEGER NOT NULL, PRIMARY KEY(table_name, old_id));" ]
    |> List.iter (fun sql ->
      use cmd = new SqliteCommand(sql, conn)
      cmd.ExecuteNonQuery() |> ignore)

    let entries =
      [ mkLogEntry 1L 21L 1L Insert "legacy_account" (mkRow [ "id", Integer 30; "name", String "Bob" ])
        mkLogEntry
          2L
          21L
          2L
          Insert
          "invoice"
          (mkRow [ "id", Integer 70; "legacy_account_id", Integer 999; "total", Real 10.0 ]) ]

    let replayResult =
      replayDrainEntries conn plan entries emptyIdMappings |> fun t -> t.Result

    match replayResult with
    | Ok _ -> failwith "Expected replay to fail when FK mapping is missing"
    | Error ex -> Assert.Contains("Replay failed for txn 21", ex.Message)

    use countAccountCmd = new SqliteCommand("SELECT COUNT(*) FROM account", conn)
    let accountCount = countAccountCmd.ExecuteScalar() |> unbox<int64>
    Assert.Equal(0L, accountCount)

    use countInvoiceCmd = new SqliteCommand("SELECT COUNT(*) FROM invoice", conn)
    let invoiceCount = countInvoiceCmd.ExecuteScalar() |> unbox<int64>
    Assert.Equal(0L, invoiceCount)

    use countMappingCmd = new SqliteCommand("SELECT COUNT(*) FROM _id_mapping", conn)
    let mappingCount = countMappingCmd.ExecuteScalar() |> unbox<int64>
    Assert.Equal(0L, mappingCount)

    conn.Close()
    Directory.Delete(tempDir, true)

[<Fact>]
let ``migration status reports old and new database markers and counts`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_status_report_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let newDbPath = Path.Combine(tempDir, "new.db")

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'recording');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 1, 'insert', 'student', '{\"id\":1,\"name\":\"A\"}');"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 2, 'update', 'student', '{\"id\":1,\"name\":\"B\"}');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  use newConn = openSqliteConnection newDbPath

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'migrating');"
    "CREATE TABLE _schema_identity(id INTEGER PRIMARY KEY CHECK (id = 0), schema_hash TEXT NOT NULL, schema_commit TEXT, created_utc TEXT NOT NULL);"
    "INSERT INTO _schema_identity(id, schema_hash, schema_commit, created_utc) VALUES (0, '1111222233334444', 'abc1234', '2026-02-18T00:00:00.0000000Z');"
    "CREATE TABLE _migration_progress(id INTEGER PRIMARY KEY CHECK (id = 0), last_replayed_log_id INTEGER NOT NULL, drain_completed INTEGER NOT NULL);"
    "INSERT INTO _migration_progress(id, last_replayed_log_id, drain_completed) VALUES (0, 1, 0);"
    "CREATE TABLE _id_mapping(table_name TEXT NOT NULL, old_id INTEGER NOT NULL, new_id INTEGER NOT NULL, PRIMARY KEY(table_name, old_id));"
    "INSERT INTO _id_mapping(table_name, old_id, new_id) VALUES ('student', 1, 101);"
    "INSERT INTO _id_mapping(table_name, old_id, new_id) VALUES ('student', 2, 102);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let statusResult = getStatus oldDbPath (Some newDbPath) |> fun t -> t.Result

  match statusResult with
  | Error ex -> failwith $"Expected status read to succeed, got {ex.Message}"
  | Ok report ->
    Assert.Equal(Some "recording", report.oldMarkerStatus)
    Assert.Equal(2L, report.migrationLogEntries)
    Assert.Equal(Some 1L, report.pendingReplayEntries)
    Assert.Equal(Some 2L, report.idMappingEntries)
    Assert.Equal(Some "migrating", report.newMigrationStatus)
    Assert.Equal(Some true, report.idMappingTablePresent)
    Assert.Equal(Some true, report.migrationProgressTablePresent)
    Assert.Equal(Some "1111222233334444", report.schemaIdentityHash)
    Assert.Equal(Some "abc1234", report.schemaIdentityCommit)

  oldConn.Close()
  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``migration status handles databases without migration tables`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_status_nomarker_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")

  use oldConn = openSqliteConnection oldDbPath

  use initCmd =
    new SqliteCommand("CREATE TABLE student(id INTEGER PRIMARY KEY, name TEXT NOT NULL);", oldConn)

  initCmd.ExecuteNonQuery() |> ignore

  let statusResult = getStatus oldDbPath None |> fun t -> t.Result

  match statusResult with
  | Error ex -> failwith $"Expected status read without migration tables to succeed, got {ex.Message}"
  | Ok report ->
    Assert.Equal(None, report.oldMarkerStatus)
    Assert.Equal(0L, report.migrationLogEntries)
    Assert.Equal(None, report.pendingReplayEntries)
    Assert.Equal(None, report.idMappingEntries)
    Assert.Equal(None, report.newMigrationStatus)
    Assert.Equal(None, report.idMappingTablePresent)
    Assert.Equal(None, report.migrationProgressTablePresent)
    Assert.Equal(None, report.schemaIdentityHash)
    Assert.Equal(None, report.schemaIdentityCommit)

  oldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``migration status reports cleanup state after cutover`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_status_cutover_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let newDbPath = Path.Combine(tempDir, "new.db")

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'draining');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 1, 'insert', 'student', '{\"id\":1,\"name\":\"A\"}');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  use newConn = openSqliteConnection newDbPath

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'ready');"
    "CREATE TABLE _schema_identity(id INTEGER PRIMARY KEY CHECK (id = 0), schema_hash TEXT NOT NULL, schema_commit TEXT, created_utc TEXT NOT NULL);"
    "INSERT INTO _schema_identity(id, schema_hash, schema_commit, created_utc) VALUES (0, '9999aaaabbbbcccc', 'deadbeef', '2026-02-18T00:00:00.0000000Z');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let statusResult = getStatus oldDbPath (Some newDbPath) |> fun t -> t.Result

  match statusResult with
  | Error ex -> failwith $"Expected status read after cutover cleanup to succeed, got {ex.Message}"
  | Ok report ->
    Assert.Equal(Some "draining", report.oldMarkerStatus)
    Assert.Equal(1L, report.migrationLogEntries)
    Assert.Equal(Some "ready", report.newMigrationStatus)
    Assert.Equal(Some 0L, report.pendingReplayEntries)
    Assert.Equal(Some 0L, report.idMappingEntries)
    Assert.Equal(Some false, report.idMappingTablePresent)
    Assert.Equal(Some false, report.migrationProgressTablePresent)
    Assert.Equal(Some "9999aaaabbbbcccc", report.schemaIdentityHash)
    Assert.Equal(Some "deadbeef", report.schemaIdentityCommit)

  oldConn.Close()
  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cutover sets ready status and drops id mapping and progress tables`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cutover_success_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let newDbPath = Path.Combine(tempDir, "new.db")

  use newConn = openSqliteConnection newDbPath

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'migrating');"
    "CREATE TABLE _migration_progress(id INTEGER PRIMARY KEY CHECK (id = 0), last_replayed_log_id INTEGER NOT NULL, drain_completed INTEGER NOT NULL);"
    "INSERT INTO _migration_progress(id, last_replayed_log_id, drain_completed) VALUES (0, 10, 1);"
    "CREATE TABLE _id_mapping(table_name TEXT NOT NULL, old_id INTEGER NOT NULL, new_id INTEGER NOT NULL, PRIMARY KEY(table_name, old_id));"
    "INSERT INTO _id_mapping(table_name, old_id, new_id) VALUES ('student', 1, 101);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let cutoverResult = runCutover newDbPath |> fun t -> t.Result

  match cutoverResult with
  | Error ex -> failwith $"Expected cutover to succeed, got {ex.Message}"
  | Ok result ->
    Assert.Equal("migrating", result.previousStatus)
    Assert.True(result.idMappingDropped)
    Assert.True(result.migrationProgressDropped)

  use verifyStatusCmd =
    new SqliteCommand("SELECT status FROM _migration_status WHERE id = 0", newConn)

  let statusValue = verifyStatusCmd.ExecuteScalar() |> string
  Assert.Equal("ready", statusValue)

  use existsCmd =
    new SqliteCommand("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_id_mapping' LIMIT 1", newConn)

  let idMappingExists = existsCmd.ExecuteScalar()
  Assert.True(isNull idMappingExists)

  use progressExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_progress' LIMIT 1",
      newConn
    )

  let progressExists = progressExistsCmd.ExecuteScalar()
  Assert.True(isNull progressExists)

  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cutover is idempotent when migration status is already ready`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cutover_ready_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let newDbPath = Path.Combine(tempDir, "new.db")

  use newConn = openSqliteConnection newDbPath

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'ready');"
    "CREATE TABLE _schema_identity(id INTEGER PRIMARY KEY CHECK (id = 0), schema_hash TEXT NOT NULL, schema_commit TEXT, created_utc TEXT NOT NULL);"
    "INSERT INTO _schema_identity(id, schema_hash, schema_commit, created_utc) VALUES (0, '9999aaaabbbbcccc', 'deadbeef', '2026-02-18T00:00:00.0000000Z');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let cutoverResult = runCutover newDbPath |> fun t -> t.Result

  match cutoverResult with
  | Error ex -> failwith $"Expected idempotent cutover to succeed, got {ex.Message}"
  | Ok result ->
    Assert.Equal("ready", result.previousStatus)
    Assert.False(result.idMappingDropped)
    Assert.False(result.migrationProgressDropped)

  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cutover fails when migration status table is missing`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cutover_missing_status_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let newDbPath = Path.Combine(tempDir, "new.db")

  use newConn = openSqliteConnection newDbPath

  use initCmd =
    new SqliteCommand(
      "CREATE TABLE _id_mapping(table_name TEXT NOT NULL, old_id INTEGER NOT NULL, new_id INTEGER NOT NULL, PRIMARY KEY(table_name, old_id));",
      newConn
    )

  initCmd.ExecuteNonQuery() |> ignore

  let cutoverResult = runCutover newDbPath |> fun t -> t.Result

  match cutoverResult with
  | Ok _ -> failwith "Expected cutover to fail when _migration_status table is missing"
  | Error ex -> Assert.Contains("_migration_status table is missing", ex.Message)

  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cutover fails when drain has not completed`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cutover_not_drained_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let newDbPath = Path.Combine(tempDir, "new.db")

  use newConn = openSqliteConnection newDbPath

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'migrating');"
    "CREATE TABLE _migration_progress(id INTEGER PRIMARY KEY CHECK (id = 0), last_replayed_log_id INTEGER NOT NULL, drain_completed INTEGER NOT NULL);"
    "INSERT INTO _migration_progress(id, last_replayed_log_id, drain_completed) VALUES (0, 100, 0);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let cutoverResult = runCutover newDbPath |> fun t -> t.Result

  match cutoverResult with
  | Ok _ -> failwith "Expected cutover to fail when drain is not complete"
  | Error ex -> Assert.Contains("Drain is not complete", ex.Message)

  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``archive old moves database to archive directory`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cleanup_old_success_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let expectedArchivePath = Path.Combine(tempDir, "archive", "old.db")

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'draining');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 1, 'insert', 'student', '{\"id\":1,\"name\":\"A\"}');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  oldConn.Close()

  let archiveResult = runArchiveOld tempDir oldDbPath |> fun t -> t.Result

  match archiveResult with
  | Error ex -> failwith $"Expected archive old to succeed, got {ex.Message}"
  | Ok result ->
    Assert.Equal(Some "draining", result.previousMarkerStatus)
    Assert.Equal(expectedArchivePath, result.archivePath)
    Assert.False(result.replacedExistingArchive)

  Assert.False(File.Exists oldDbPath)
  Assert.True(File.Exists expectedArchivePath)

  use verifyConn = openSqliteConnection expectedArchivePath

  use markerExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_marker' LIMIT 1",
      verifyConn
    )

  let markerExists = markerExistsCmd.ExecuteScalar()
  Assert.False(isNull markerExists)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``archive old moves database to archive even when migration tables are missing`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cleanup_old_missing_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let expectedArchivePath = Path.Combine(tempDir, "archive", "old.db")

  use oldConn = openSqliteConnection oldDbPath

  use initCmd =
    new SqliteCommand("CREATE TABLE student(id INTEGER PRIMARY KEY, name TEXT NOT NULL);", oldConn)

  initCmd.ExecuteNonQuery() |> ignore
  oldConn.Close()

  let archiveResult = runArchiveOld tempDir oldDbPath |> fun t -> t.Result

  match archiveResult with
  | Error ex -> failwith $"Expected archive old to succeed, got {ex.Message}"
  | Ok result ->
    Assert.Equal(None, result.previousMarkerStatus)
    Assert.Equal(expectedArchivePath, result.archivePath)
    Assert.False(result.replacedExistingArchive)

  Assert.False(File.Exists oldDbPath)
  Assert.True(File.Exists expectedArchivePath)
  Directory.Delete(tempDir, true)

[<Fact>]
let ``archive old fails while marker status is recording`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cleanup_old_recording_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'recording');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  let archiveResult = runArchiveOld tempDir oldDbPath |> fun t -> t.Result

  match archiveResult with
  | Ok _ -> failwith "Expected archive old to fail while marker status is recording"
  | Error ex -> Assert.Contains("recording mode", ex.Message)

  oldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli status prints cutover-complete cleanup state`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_status_cutover_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-1111222233334444.sqlite")

  let newDbPath = Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)
  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'draining');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 1, 'insert', 'student', '{\"id\":1,\"name\":\"A\"}');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  use newConn = openSqliteConnection newDbPath

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'ready');"
    "CREATE TABLE _schema_identity(id INTEGER PRIMARY KEY CHECK (id = 0), schema_hash TEXT NOT NULL, schema_commit TEXT, created_utc TEXT NOT NULL);"
    "INSERT INTO _schema_identity(id, schema_hash, schema_commit, created_utc) VALUES (0, '0123456789abcdef', 'fixture-commit', '2026-01-01T00:00:00.0000000Z');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let exitCode, stdOut, stdErr =
    runMigCliInDirectory (Some tempDir) [ "status"; "--assembly"; assemblyPath; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains("Marker status: draining", stdOut)
  Assert.Contains("Migration status: ready", stdOut)
  Assert.Contains("Schema hash: 0123456789abcdef", stdOut)
  Assert.Contains("Schema commit: fixture-commit", stdOut)
  Assert.Contains("Pending replay entries: 0 (cutover complete)", stdOut)
  Assert.Contains("_id_mapping: removed", stdOut)
  Assert.Contains("_migration_progress: removed", stdOut)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")

  oldConn.Close()
  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli status supports inferred new-only inspection`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_status_new_only_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let schemaPath = Path.Combine(tempDir, "Schema.fs")

  File.WriteAllText(schemaPath, "module Schema\n")
  let newDbPath = deriveDeterministicNewDbPathFromSchemaFile tempDir schemaPath

  use newConn = openSqliteConnection newDbPath

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'ready');"
    "CREATE TABLE _schema_identity(id INTEGER PRIMARY KEY CHECK (id = 0), schema_hash TEXT NOT NULL, schema_commit TEXT, created_utc TEXT NOT NULL);"
    "INSERT INTO _schema_identity(id, schema_hash, schema_commit, created_utc) VALUES (0, 'abcddcba12344321', 'cafebabe', '2026-02-18T00:00:00.0000000Z');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let exitCode, stdOut, stdErr = runMigCliInDirectory (Some tempDir) [ "status" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Old database: n/a (not inferred)", stdOut)
  Assert.Contains("Marker status: n/a", stdOut)
  Assert.Contains("Migration log entries: n/a", stdOut)
  Assert.Contains($"New database: {newDbPath}", stdOut)
  Assert.Contains("Migration status: ready", stdOut)
  Assert.Contains("Schema hash: abcddcba12344321", stdOut)
  Assert.Contains("Schema commit: cafebabe", stdOut)
  Assert.Contains("Pending replay entries: n/a (old database unavailable)", stdOut)
  Assert.Contains("_id_mapping: removed", stdOut)
  Assert.Contains("_migration_progress: removed", stdOut)

  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli status infers new database from Schema fs`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_status_schema_fs_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-fedcba9876543210.sqlite")

  let newDbPath = Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)
  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  createStudentDatabase oldDbPath [ "Alice" ] None
  createStudentDatabase newDbPath [ "Alice" ] (Some "ready")

  let exitCode, stdOut, stdErr =
    runMigCliInDirectory (Some tempDir) [ "status"; "--assembly"; assemblyPath; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains($"New database: {newDbPath}", stdOut)
  Assert.Contains("Migration status: ready", stdOut)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli status can use compiled generated module from assembly`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_status_compiled_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-fedcba9876543210.sqlite")

  let newDbPath = Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)
  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  createStudentDatabase oldDbPath [ "Alice" ] None
  createStudentDatabase newDbPath [ "Alice" ] (Some "ready")

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "status"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains($"New database: {newDbPath}", stdOut)
  Assert.Contains("Migration status: ready", stdOut)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli root help shows current command surface`` () =
  assertCliHelpOutput
    [ "--help" ]
    "USAGE: mig [--help] [--version] [<subcommand> [<options>]]"
    [ "init <options>"
      "codegen <options>"
      "migrate <options>"
      "offline <options>"
      "plan <options>"
      "drain <options>"
      "cutover <options>"
      "archive-old <options>"
      "reset <options>"
      "status <options>"
      "--version, -v" ]

[<Fact>]
let ``cli version flag prints version`` () =
  let expectedVersion =
    let version = typeof<Mig.Program.Command>.Assembly.GetName().Version

    if isNull version then
      "unknown"
    else
      $"{version.Major}.{version.Minor}.{version.Build}"

  let exitCode, stdOut, stdErr = runMigCli [ "--version" ]
  Assert.Equal(0, exitCode)
  Assert.Contains(expectedVersion, stdOut)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")

[<Fact>]
let ``cli subcommand help shows usage and options`` () =
  let cases: (string list * string * string list) list =
    [ ([ "init"; "--help" ],
       "USAGE: mig init [--help] [--dir <path>] [--assembly <path>] [--module <name>]",
       [ "--dir, -d <path>"; "--assembly, -a <path>"; "--module, -m <name>" ])
      ([ "codegen"; "--help" ],
       "USAGE: mig codegen [--help] [--dir <path>] [--assembly <path>]",
       [ "--dir, -d <path>"
         "--assembly, -a <path>"
         "--schema-module, -s <name>"
         "--module, -m <name>"
         "--output, -o <path>" ])
      ([ "migrate"; "--help" ],
       "USAGE: mig migrate [--help] [--dir <path>] [--assembly <path>] [--module <name>]",
       [ "--dir, -d <path>"; "--assembly, -a <path>"; "--module, -m <name>" ])
      ([ "offline"; "--help" ],
       "USAGE: mig offline [--help] [--dir <path>] [--assembly <path>] [--module <name>]",
       [ "--dir, -d <path>"; "--assembly, -a <path>"; "--module, -m <name>" ])
      ([ "plan"; "--help" ],
       "USAGE: mig plan [--help] [--dir <path>] [--assembly <path>] [--module <name>]",
       [ "--dir, -d <path>"; "--assembly, -a <path>"; "--module, -m <name>" ])
      ([ "drain"; "--help" ],
       "USAGE: mig drain [--help] [--dir <path>] [--assembly <path>] [--module <name>]",
       [ "--dir, -d <path>"; "--assembly, -a <path>"; "--module, -m <name>" ])
      ([ "cutover"; "--help" ],
       "USAGE: mig cutover [--help] [--dir <path>] [--assembly <path>] [--module <name>]",
       [ "--dir, -d <path>"; "--assembly, -a <path>"; "--module, -m <name>" ])
      ([ "archive-old"; "--help" ],
       "USAGE: mig archive-old [--help] [--dir <path>] [--assembly <path>]",
       [ "--dir, -d <path>"; "--assembly, -a <path>"; "--module, -m <name>" ])
      ([ "reset"; "--help" ],
       "USAGE: mig reset [--help] [--dir <path>] [--assembly <path>] [--module <name>]",
       [ "--dir, -d <path>"
         "--assembly, -a <path>"
         "--module, -m <name>"
         "--dry-run" ])
      ([ "status"; "--help" ],
       "USAGE: mig status [--help] [--dir <path>] [--assembly <path>] [--module <name>]",
       [ "--dir, -d <path>"; "--assembly, -a <path>"; "--module, -m <name>" ]) ]

  for args, expectedUsage, expectedFragments in cases do
    assertCliHelpOutput args expectedUsage expectedFragments

[<Fact>]
let ``cli codegen requires assembly`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_codegen_requires_assembly_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let exitCode, stdOut, stdErr = runMigCli [ "codegen"; "-d"; tempDir ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("codegen failed:", stdErr)
  Assert.Contains("requires --assembly", stdErr)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli codegen prefers Schema fsproj assembly name when present`` () =
  let sourceBuildOutputDirectory =
    typeof<CompiledSchemaSourceFixture.Marker>.Assembly.Location
    |> Path.GetDirectoryName

  let tempDir = createTempFsprojWithBuildOutput "Test" sourceBuildOutputDirectory
  let schemaPath = Path.Combine(tempDir, "Schema.fs")
  let outputPath = Path.Combine(tempDir, "Db.fs")
  let testAssemblyPath = Path.Combine(tempDir, "bin", "Debug", "net10.0", "Test.dll")

  let inferredAssemblyPath =
    Path.Combine(tempDir, "bin", "Debug", "net10.0", "TruthMasker.Schema.dll")

  File.WriteAllText(
    Path.Combine(tempDir, "Schema.fsproj"),
    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>TruthMasker.Schema</AssemblyName></PropertyGroup></Project>"
  )

  File.Copy(testAssemblyPath, inferredAssemblyPath, true)
  File.Delete(testAssemblyPath)

  File.WriteAllText(
    schemaPath,
    "module Schema\n\nopen MigLib.Db\n\n[<AutoIncPK \"id\">]\ntype Student = { id: int64; name: string }\n"
  )

  let exitCode, stdOut, stdErr =
    runMigCliInDirectory (Some tempDir) [ "codegen"; "--schema-module"; "CompiledSchemaSourceFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Code generation complete.", stdOut)
  Assert.Contains($"Compiled assembly: {Path.GetFullPath inferredAssemblyPath}", stdOut)
  Assert.True(File.Exists outputPath, $"Expected generated file at: {outputPath}")

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli codegen defaults to Db fs and Db module in compiled mode`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_codegen_defaults_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let schemaPath = Path.Combine(tempDir, "Schema.fs")
  let outputPath = Path.Combine(tempDir, "Db.fs")
  let assemblyPath = typeof<CompiledSchemaSourceFixture.Marker>.Assembly.Location

  File.WriteAllText(
    schemaPath,
    "module Schema\n\nopen MigLib.Db\n\n[<AutoIncPK \"id\">]\ntype Student = { id: int64; name: string }\n"
  )

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "codegen"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--schema-module"
        "CompiledSchemaSourceFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Code generation complete.", stdOut)
  Assert.True(File.Exists outputPath, $"Expected generated file at: {outputPath}")

  let generated = File.ReadAllText outputPath

  Assert.Contains("module Db", generated)
  Assert.Contains("let DbApp =", generated)
  Assert.Contains("let DbFileForInstance", generated)
  Assert.Contains("let Schema:", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli codegen can generate Db module from compiled schema assembly`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_codegen_compiled_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let schemaPath = Path.Combine(tempDir, "Schema.fs")
  let outputPath = Path.Combine(tempDir, "Db.fs")
  let projectPath = Path.Combine(tempDir, "Db.fsproj")
  let assemblyPath = typeof<CompiledSchemaSourceFixture.Marker>.Assembly.Location

  File.WriteAllText(
    schemaPath,
    "module Schema\n\nopen MigLib.Db\n\n[<AutoIncPK \"id\">]\ntype Student = { id: int64; name: string }\n"
  )

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "codegen"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--schema-module"
        "CompiledSchemaSourceFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Code generation complete.", stdOut)
  Assert.Contains($"Schema source: {schemaPath}", stdOut)
  Assert.Contains($"Compiled assembly: {Path.GetFullPath assemblyPath}", stdOut)
  Assert.Contains("Schema module: CompiledSchemaSourceFixture", stdOut)
  Assert.Contains("Generated module: Db", stdOut)
  Assert.Contains($"Output file: {outputPath}", stdOut)
  Assert.True(File.Exists outputPath, $"Expected generated file at: {outputPath}")
  Assert.False(File.Exists projectPath, $"Did not expect generated project file at: {projectPath}")

  let generated = File.ReadAllText outputPath

  Assert.Contains("module Db", generated)
  Assert.Contains($"let DbApp = \"{DirectoryInfo(tempDir).Name}\"", generated)
  Assert.Contains("let DbApp =", generated)
  Assert.Contains("let DbFileForInstance", generated)
  Assert.Contains("let SchemaHash =", generated)
  Assert.Contains("let SchemaIdentity:", generated)
  Assert.Contains("let Schema:", generated)
  Assert.Contains("static member SelectByName (name: string) (tx: SqliteTransaction)", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli codegen rejects schema module argument without assembly`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_codegen_schema_module_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let exitCode, stdOut, stdErr =
    runMigCli [ "codegen"; "-d"; tempDir; "--schema-module"; "CompiledSchemaSourceFixture" ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("codegen failed:", stdErr)
  Assert.Contains("requires --assembly", stdErr)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli codegen reports missing Schema fs when assembly mode is selected`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_codegen_schema_fs_only_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let assemblyPath = typeof<CompiledSchemaSourceFixture.Marker>.Assembly.Location

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "codegen"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--schema-module"
        "CompiledSchemaSourceFixture" ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("codegen failed: Schema source file was not found:", stdErr)
  Assert.Contains(Path.Combine(tempDir, "Schema.fs"), stdErr)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli codegen rejects output paths outside schema directory`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_codegen_output_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let schemaPath = Path.Combine(tempDir, "Schema.fs")
  let assemblyPath = typeof<CompiledSchemaSourceFixture.Marker>.Assembly.Location

  File.WriteAllText(
    schemaPath,
    "module Schema\n\nopen MigLib.Db\n\n[<AutoIncPK \"id\">]\ntype Student = { id: int64; name: string }\n"
  )

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "codegen"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--schema-module"
        "CompiledSchemaSourceFixture"
        "--output"
        "nested/Generated.fs" ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("same directory as Schema.fs", stdErr)

  let disallowedOutput = Path.Combine(tempDir, "nested", "Generated.fs")
  Assert.False(File.Exists disallowedOutput, $"Unexpected generated file at: {disallowedOutput}")

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli codegen rejects invalid module names without writing output`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_codegen_module_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let schemaPath = Path.Combine(tempDir, "Schema.fs")
  let outputPath = Path.Combine(tempDir, "Generated.fs")
  let assemblyPath = typeof<CompiledSchemaSourceFixture.Marker>.Assembly.Location

  File.WriteAllText(
    schemaPath,
    "module Schema\n\nopen MigLib.Db\n\n[<AutoIncPK \"id\">]\ntype Student = { id: int64; name: string }\n"
  )

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "codegen"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--schema-module"
        "CompiledSchemaSourceFixture"
        "--module"
        "bad-name"
        "--output"
        "Generated.fs" ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("valid F# module identifier", stdErr)
  Assert.False(File.Exists outputPath, $"Unexpected generated file at: {outputPath}")

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli cutover returns error when drain not complete`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_cutover_not_drained_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let newDbPath = Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)
  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  use newConn = openSqliteConnection newDbPath

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'migrating');"
    "CREATE TABLE _migration_progress(id INTEGER PRIMARY KEY CHECK (id = 0), last_replayed_log_id INTEGER NOT NULL, drain_completed INTEGER NOT NULL);"
    "INSERT INTO _migration_progress(id, last_replayed_log_id, drain_completed) VALUES (0, 12, 0);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "cutover"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("cutover failed:", stdErr)
  Assert.Contains("Drain is not complete", stdErr)

  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli cutover blocks when old marker indicates replay divergence risk`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_cutover_divergence_marker_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-a1b2c3d4e5f60718.sqlite")

  let newDbPath = Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)
  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'recording');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  use newConn = openSqliteConnection newDbPath

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'migrating');"
    "CREATE TABLE _migration_progress(id INTEGER PRIMARY KEY CHECK (id = 0), last_replayed_log_id INTEGER NOT NULL, drain_completed INTEGER NOT NULL);"
    "INSERT INTO _migration_progress(id, last_replayed_log_id, drain_completed) VALUES (0, 0, 1);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  oldConn.Close()
  newConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "cutover"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("cutover failed:", stdErr)
  Assert.Contains("Cutover blocked: old marker status is recording", stdErr)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli cutover blocks when old migration log has unreplayed entries beyond checkpoint`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_cutover_divergence_pending_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-a1b2c3d4e5f60718.sqlite")

  let newDbPath = Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)
  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'draining');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 1, 'insert', 'student', '{\"id\":1,\"name\":\"Alice\"}');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  use newConn = openSqliteConnection newDbPath

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'migrating');"
    "CREATE TABLE _migration_progress(id INTEGER PRIMARY KEY CHECK (id = 0), last_replayed_log_id INTEGER NOT NULL, drain_completed INTEGER NOT NULL);"
    "INSERT INTO _migration_progress(id, last_replayed_log_id, drain_completed) VALUES (0, 0, 1);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  oldConn.Close()
  newConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "cutover"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("cutover failed:", stdErr)
  Assert.Contains("Cutover blocked: old _migration_log has 1 unreplayed entry", stdErr)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli drain reports missing schema source clearly`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_drain_missing_schema_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let exitCode, stdOut, stdErr = runMigCli [ "drain"; "-d"; tempDir ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("drain failed:", stdErr)
  Assert.Contains("requires --assembly", stdErr)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli drain reports non-deterministic old database names clearly`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_drain_bad_old_name_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore
  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location
  let nonDeterministicOldPath = Path.Combine(tempDir, "old.sqlite")

  use oldConn = openSqliteConnection nonDeterministicOldPath
  oldConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "drain"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("drain failed:", stdErr)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli archive-old archives old database into archive directory`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_cleanup_old_success_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-99990000aaaabbbb.sqlite")

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  let expectedArchivePath =
    Path.Combine(tempDir, "archive", Path.GetFileName oldDbPath)

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'draining');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  oldConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "archive-old"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.Contains("Old database archive complete.", stdOut)
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains("Previous marker status: draining", stdOut)
  Assert.Contains($"Archived database: {expectedArchivePath}", stdOut)
  Assert.Contains("Replaced existing archive: no", stdOut)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")

  Assert.False(File.Exists oldDbPath)
  Assert.True(File.Exists expectedArchivePath)

  use verifyConn = openSqliteConnection expectedArchivePath

  use markerExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_marker' LIMIT 1",
      verifyConn
    )

  let markerExists = markerExistsCmd.ExecuteScalar()
  Assert.False(isNull markerExists)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``archive old replaces existing archive database when moving`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_archive_old_replace_archive_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let archiveDir = Path.Combine(tempDir, "archive")
  let archivePath = Path.Combine(archiveDir, "old.db")
  Directory.CreateDirectory archiveDir |> ignore
  File.WriteAllText(archivePath, "stale archive")

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'draining');"
    "CREATE TABLE student(id INTEGER PRIMARY KEY, name TEXT NOT NULL);"
    "INSERT INTO student(id, name) VALUES (1, 'Alice');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  oldConn.Close()

  let archiveResult = runArchiveOld tempDir oldDbPath |> fun t -> t.Result

  match archiveResult with
  | Error ex -> failwith $"Expected archive old to succeed, got {ex.Message}"
  | Ok result ->
    Assert.Equal(archivePath, result.archivePath)
    Assert.True(result.replacedExistingArchive)

  Assert.False(File.Exists oldDbPath)
  Assert.True(File.Exists archivePath)

  use verifyArchiveConn = openSqliteConnection archivePath

  use studentCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM student", verifyArchiveConn)

  let studentCount = studentCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, studentCount)

  verifyArchiveConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli archive-old returns error while recording`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_cleanup_old_recording_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-ccccddddeeeeffff.sqlite")

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'recording');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  oldConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "archive-old"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("archive-old failed:", stdErr)
  Assert.Contains("Old database is still in recording mode.", stdErr)

  use verifyConn = openSqliteConnection oldDbPath

  use markerExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_marker' LIMIT 1",
      verifyConn
    )

  let markerExists = markerExistsCmd.ExecuteScalar()
  Assert.False(isNull markerExists)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli reset clears old migration artifacts and deletes non-ready new database`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_reset_success_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-1122334455667788.sqlite")

  let newDbPath = Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)
  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'recording');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 1, 'insert', 'student', '{\"id\":1,\"name\":\"Alice\"}');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  oldConn.Close()

  use newConn = openSqliteConnection newDbPath

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'migrating');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  newConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "reset"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Migration reset complete.", stdOut)
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains("Previous old marker status: recording", stdOut)
  Assert.Contains("Dropped _migration_marker: yes", stdOut)
  Assert.Contains("Dropped _migration_log: yes", stdOut)
  Assert.Contains($"New database: {newDbPath}", stdOut)
  Assert.Contains("New database existed: yes", stdOut)
  Assert.Contains("Previous new migration status: migrating", stdOut)
  Assert.Contains("Deleted new database: yes", stdOut)

  use verifyOldConn = openSqliteConnection oldDbPath

  use markerExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_marker' LIMIT 1",
      verifyOldConn
    )

  let markerExists = markerExistsCmd.ExecuteScalar()
  Assert.True(isNull markerExists)

  use logExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_log' LIMIT 1",
      verifyOldConn
    )

  let logExists = logExistsCmd.ExecuteScalar()
  Assert.True(isNull logExists)

  Assert.False(File.Exists newDbPath)

  verifyOldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli reset dry-run reports planned actions without mutating old or new databases`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_reset_dry_run_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-0011223344556677.sqlite")

  let newDbPath = Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)
  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'recording');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 1, 'insert', 'student', '{\"id\":1,\"name\":\"Alice\"}');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  oldConn.Close()

  use newConn = openSqliteConnection newDbPath

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'migrating');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  newConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "reset"
        "--dry-run"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Migration reset dry run.", stdOut)
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains("Previous old marker status: recording", stdOut)
  Assert.Contains("Would drop _migration_marker: yes", stdOut)
  Assert.Contains("Would drop _migration_log: yes", stdOut)
  Assert.Contains($"New database: {newDbPath}", stdOut)
  Assert.Contains("New database existed: yes", stdOut)
  Assert.Contains("Previous new migration status: migrating", stdOut)
  Assert.Contains("Would delete new database: yes", stdOut)
  Assert.Contains("Reset can be applied: yes", stdOut)

  use verifyOldConn = openSqliteConnection oldDbPath

  use markerExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_marker' LIMIT 1",
      verifyOldConn
    )

  let markerExists = markerExistsCmd.ExecuteScalar()
  Assert.False(isNull markerExists)

  use logExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_log' LIMIT 1",
      verifyOldConn
    )

  let logExists = logExistsCmd.ExecuteScalar()
  Assert.False(isNull logExists)

  Assert.True(File.Exists newDbPath)

  verifyOldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli reset dry-run can use compiled generated module from assembly`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_reset_dry_run_compiled_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-0011223344556677.sqlite")

  let newDbPath = Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)
  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'recording');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 1, 'insert', 'student', '{\"id\":1,\"name\":\"Alice\"}');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  oldConn.Close()

  use newConn = openSqliteConnection newDbPath

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'migrating');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  newConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "reset"
        "--dry-run"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Migration reset dry run.", stdOut)
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains($"New database: {newDbPath}", stdOut)
  Assert.Contains("Previous new migration status: migrating", stdOut)
  Assert.Contains("Would delete new database: yes", stdOut)
  Assert.Contains("Reset can be applied: yes", stdOut)

  Assert.True(File.Exists(newDbPath))

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli reset dry-run reports blocked ready target without mutating databases`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_reset_dry_run_ready_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-8899aabbccddeeff.sqlite")

  let newDbPath = Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)
  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'recording');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  oldConn.Close()

  use newConn = openSqliteConnection newDbPath

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'ready');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  newConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "reset"
        "--dry-run"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Migration reset dry run.", stdOut)
  Assert.Contains("Reset can be applied: no", stdOut)
  Assert.Contains("Would delete new database: no", stdOut)
  Assert.Contains("Blocked reason: Refusing reset because new database status is ready", stdOut)

  use verifyOldConn = openSqliteConnection oldDbPath

  use markerExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_marker' LIMIT 1",
      verifyOldConn
    )

  let markerExists = markerExistsCmd.ExecuteScalar()
  Assert.False(isNull markerExists)

  use logExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_log' LIMIT 1",
      verifyOldConn
    )

  let logExists = logExistsCmd.ExecuteScalar()
  Assert.False(isNull logExists)

  Assert.True(File.Exists newDbPath)

  verifyOldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli reset refuses to delete ready new database and leaves old artifacts intact`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_reset_ready_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-8899aabbccddeeff.sqlite")

  let newDbPath = Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)
  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'recording');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  oldConn.Close()

  use newConn = openSqliteConnection newDbPath

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'ready');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  newConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "reset"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("reset failed:", stdErr)
  Assert.Contains("Refusing reset because new database status is ready", stdErr)

  use verifyOldConn = openSqliteConnection oldDbPath

  use markerExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_marker' LIMIT 1",
      verifyOldConn
    )

  let markerExists = markerExistsCmd.ExecuteScalar()
  Assert.False(isNull markerExists)

  use logExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_log' LIMIT 1",
      verifyOldConn
    )

  let logExists = logExistsCmd.ExecuteScalar()
  Assert.False(isNull logExists)

  Assert.True(File.Exists newDbPath)

  verifyOldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli migrate requires assembly`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_migrate_requires_assembly_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let exitCode, stdOut, stdErr = runMigCli [ "migrate"; "-d"; tempDir ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("migrate failed:", stdErr)
  Assert.Contains("requires --assembly", stdErr)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli init requires assembly`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_init_requires_assembly_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let exitCode, stdOut, stdErr = runMigCliInDirectory (Some tempDir) [ "init" ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("init failed:", stdErr)
  Assert.Contains("requires --assembly", stdErr)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli init skips when compiled-module database already exists`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_init_skip_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  let expectedDbPath = Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)

  use existingConn = openSqliteConnection expectedDbPath

  use initCmd =
    new SqliteCommand("CREATE TABLE sentinel(id INTEGER PRIMARY KEY, value TEXT NOT NULL);", existingConn)

  initCmd.ExecuteNonQuery() |> ignore
  existingConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCliInDirectory (Some tempDir) [ "init"; "--assembly"; assemblyPath; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Init skipped.", stdOut)
  Assert.Contains($"Database already present for current schema: {expectedDbPath}", stdOut)

  use verifyConn = openSqliteConnection expectedDbPath

  use sentinelCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM sentinel", verifyConn)

  let sentinelCount = sentinelCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(0L, sentinelCount)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli init can use compiled generated module from assembly`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_init_compiled_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  let expectedDbPath = Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "init"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Init complete.", stdOut)
  Assert.Contains($"Compiled assembly: {Path.GetFullPath assemblyPath}", stdOut)
  Assert.Contains("Compiled module: CompiledSchemaFixture", stdOut)
  Assert.Contains($"Database: {expectedDbPath}", stdOut)
  Assert.True(File.Exists expectedDbPath)

  use verifyConn = openSqliteConnection expectedDbPath

  use tableExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fixture_student' LIMIT 1",
      verifyConn
    )

  let tableExists = tableExistsCmd.ExecuteScalar()
  Assert.False(isNull tableExists)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli init prefers non-schema project assembly when Schema fsproj is present`` () =
  let runtimeBuildOutputDirectory =
    typeof<CompiledSchemaFixture.Marker>.Assembly.Location |> Path.GetDirectoryName

  let schemaAssemblySourcePath = typeof<Mig.Program.MigrateArgs>.Assembly.Location

  let tempDir = createTempFsprojWithBuildOutput "Test" runtimeBuildOutputDirectory

  let copiedTestAssemblyPath =
    Path.Combine(tempDir, "bin", "Debug", "net10.0", "Test.dll")

  let inferredAssemblyPath =
    Path.Combine(tempDir, "bin", "Debug", "net10.0", "TruthMasker.Core.dll")

  let schemaAssemblyPath =
    Path.Combine(tempDir, "bin", "Debug", "net10.0", "TruthMasker.Schema.dll")

  File.Delete(Path.Combine(tempDir, "Test.fsproj"))

  File.WriteAllText(
    Path.Combine(tempDir, "Core.fsproj"),
    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>TruthMasker.Core</AssemblyName></PropertyGroup></Project>"
  )

  File.WriteAllText(
    Path.Combine(tempDir, "Schema.fsproj"),
    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>TruthMasker.Schema</AssemblyName></PropertyGroup></Project>"
  )

  File.Copy(copiedTestAssemblyPath, inferredAssemblyPath, true)
  File.Delete(copiedTestAssemblyPath)
  File.Copy(schemaAssemblySourcePath, schemaAssemblyPath, true)

  let expectedDbPath = Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)

  let exitCode, stdOut, stdErr =
    runMigCliInDirectory (Some tempDir) [ "init"; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Init complete.", stdOut)
  Assert.Contains($"Compiled assembly: {Path.GetFullPath inferredAssemblyPath}", stdOut)
  Assert.Contains("Compiled module: CompiledSchemaFixture", stdOut)
  Assert.Contains($"Database: {expectedDbPath}", stdOut)
  Assert.True(File.Exists expectedDbPath)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli init asks for assembly when fsproj autodiscovery has no built dll`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_init_module_only_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  File.WriteAllText(
    Path.Combine(tempDir, "Schema.fsproj"),
    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>TruthMasker.Schema</AssemblyName></PropertyGroup></Project>"
  )

  let exitCode, stdOut, stdErr = runMigCli [ "init"; "-d"; tempDir ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("init failed:", stdErr)
  Assert.Contains("Found only 'Schema.fsproj'", stdErr)
  Assert.Contains("pass --assembly explicitly", stdErr)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli plan requires assembly`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_plan_requires_assembly_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let exitCode, stdOut, stdErr = runMigCli [ "plan"; "-d"; tempDir ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("plan failed:", stdErr)
  Assert.Contains("requires --assembly", stdErr)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli offline requires assembly`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_offline_requires_assembly_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let exitCode, stdOut, stdErr = runMigCli [ "offline"; "-d"; tempDir ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("offline failed:", stdErr)
  Assert.Contains("requires --assembly", stdErr)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli migrate can use compiled generated module from assembly`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_migrate_compiled_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-fedcba9876543210.sqlite")

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  let expectedNewDbPath =
    Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)

  use setupOldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE fixture_student(id INTEGER NOT NULL);"
    "INSERT INTO fixture_student(id) VALUES (1);"
    "INSERT INTO fixture_student(id) VALUES (2);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupOldConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "migrate"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains($"Compiled assembly: {Path.GetFullPath assemblyPath}", stdOut)
  Assert.Contains("Compiled module: CompiledSchemaFixture", stdOut)
  Assert.Contains("Schema hash: 0123456789abcdef", stdOut)
  Assert.Contains($"New database: {expectedNewDbPath}", stdOut)
  Assert.True(File.Exists expectedNewDbPath)

  use verifyConn = openSqliteConnection expectedNewDbPath

  use studentCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM fixture_student", verifyConn)

  let studentCount = studentCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(2L, studentCount)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli plan can use compiled generated module from assembly`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_plan_compiled_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-1a2b3c4d5e6f7788.sqlite")

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  let expectedNewDbPath =
    Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)

  use setupOldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE fixture_student(id INTEGER NOT NULL);"
    "INSERT INTO fixture_student(id) VALUES (1);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupOldConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCliInDirectory (Some tempDir) [ "plan"; "--assembly"; assemblyPath; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Migration plan.", stdOut)
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains($"Compiled assembly: {Path.GetFullPath assemblyPath}", stdOut)
  Assert.Contains("Compiled module: CompiledSchemaFixture", stdOut)
  Assert.Contains("Schema hash: 0123456789abcdef", stdOut)
  Assert.Contains("Schema commit: fixture-commit", stdOut)
  Assert.Contains($"New database: {expectedNewDbPath}", stdOut)
  Assert.Contains("Can run migrate now: yes", stdOut)
  Assert.False(File.Exists expectedNewDbPath)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli offline can use compiled generated module from assembly`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_offline_compiled_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-a1b2c3d4e5f60718.sqlite")

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  let expectedNewDbPath =
    Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)

  let expectedArchivePath =
    Path.Combine(tempDir, "archive", Path.GetFileName oldDbPath)

  use setupOldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE fixture_student(id INTEGER NOT NULL);"
    "INSERT INTO fixture_student(id) VALUES (1);"
    "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'draining');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupOldConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCliInDirectory (Some tempDir) [ "offline"; "--assembly"; assemblyPath; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains($"Compiled assembly: {Path.GetFullPath assemblyPath}", stdOut)
  Assert.Contains("Compiled module: CompiledSchemaFixture", stdOut)
  Assert.Contains("Schema hash: 0123456789abcdef", stdOut)
  Assert.Contains($"New database: {expectedNewDbPath}", stdOut)
  Assert.Contains("Previous old marker status: draining", stdOut)
  Assert.Contains($"Archived database: {expectedArchivePath}", stdOut)
  Assert.Contains("Replaced existing archive: no", stdOut)
  Assert.False(File.Exists oldDbPath)
  Assert.True(File.Exists expectedArchivePath)

  use verifyNewConn = openSqliteConnection expectedNewDbPath

  use studentCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM fixture_student", verifyNewConn)

  let studentCount = studentCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, studentCount)

  verifyNewConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli migrate stores compiled schema commit metadata`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_migrate_schema_commit_compiled_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-1111222233334444.sqlite")

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  let expectedNewDbPath =
    Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)

  use setupOldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE fixture_student(id INTEGER NOT NULL);"
    "INSERT INTO fixture_student(id) VALUES (1);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupOldConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCli
      [ "migrate"
        "-d"
        tempDir
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains($"New database: {expectedNewDbPath}", stdOut)

  use verifyConn = openSqliteConnection expectedNewDbPath

  use schemaIdentityCmd =
    new SqliteCommand("SELECT schema_commit FROM _schema_identity WHERE id = 0", verifyConn)

  let storedSchemaCommit = schemaIdentityCmd.ExecuteScalar() |> string
  Assert.Equal("fixture-commit", storedSchemaCommit)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli migrate auto-discovers old db from current directory with compiled module`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_migrate_auto_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-fedcba9876543210.sqlite")

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  let expectedNewDbPath =
    Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)

  use setupOldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE fixture_student(id INTEGER NOT NULL);"
    "INSERT INTO fixture_student(id) VALUES (1);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupOldConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCliInDirectory (Some tempDir) [ "migrate"; "--assembly"; assemblyPath; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains($"Compiled assembly: {Path.GetFullPath assemblyPath}", stdOut)
  Assert.Contains("Compiled module: CompiledSchemaFixture", stdOut)
  Assert.Contains($"New database: {expectedNewDbPath}", stdOut)
  Assert.True(File.Exists expectedNewDbPath)

  use verifyConn = openSqliteConnection expectedNewDbPath

  use studentCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM fixture_student", verifyConn)

  let studentCount = studentCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, studentCount)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli plan prints dry-run migration plan from compiled module without mutating databases`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_plan_dry_run_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-1a2b3c4d5e6f7788.sqlite")

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  let expectedNewDbPath =
    Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)

  use setupOldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE fixture_student(id INTEGER NOT NULL);"
    "INSERT INTO fixture_student(id) VALUES (1);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupOldConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCliInDirectory (Some tempDir) [ "plan"; "--assembly"; assemblyPath; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Migration plan.", stdOut)
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains($"Compiled assembly: {Path.GetFullPath assemblyPath}", stdOut)
  Assert.Contains("Compiled module: CompiledSchemaFixture", stdOut)
  Assert.Contains("Schema hash: 0123456789abcdef", stdOut)
  Assert.Contains($"New database: {expectedNewDbPath}", stdOut)
  Assert.Contains("Can run migrate now: yes", stdOut)
  Assert.Contains("Planned copy targets (execution order):", stdOut)
  Assert.Contains("  - fixture_student", stdOut)
  Assert.False(File.Exists expectedNewDbPath)

  use verifyOldConn = openSqliteConnection oldDbPath

  use markerExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_marker' LIMIT 1",
      verifyOldConn
    )

  let markerExists = markerExistsCmd.ExecuteScalar()
  Assert.True(isNull markerExists)

  use logExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_log' LIMIT 1",
      verifyOldConn
    )

  let logExists = logExistsCmd.ExecuteScalar()
  Assert.True(isNull logExists)

  verifyOldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli plan reports blocking drift from compiled module and keeps databases unchanged`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_plan_blocking_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-9a8b7c6d5e4f3210.sqlite")

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  let expectedNewDbPath =
    Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)

  use setupOldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE fixture_student(name TEXT NOT NULL);"
    "INSERT INTO fixture_student(name) VALUES ('Alice');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupOldConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCliInDirectory (Some tempDir) [ "plan"; "--assembly"; assemblyPath; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Migration plan.", stdOut)
  Assert.Contains("Can run migrate now: no", stdOut)
  Assert.Contains("Unsupported differences:", stdOut)
  Assert.Contains("fixture_student.id", stdOut)
  Assert.Contains("new and required", stdOut)
  Assert.False(File.Exists expectedNewDbPath)

  use verifyOldConn = openSqliteConnection oldDbPath

  use markerExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_marker' LIMIT 1",
      verifyOldConn
    )

  let markerExists = markerExistsCmd.ExecuteScalar()
  Assert.True(isNull markerExists)

  use logExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_log' LIMIT 1",
      verifyOldConn
    )

  let logExists = logExistsCmd.ExecuteScalar()
  Assert.True(isNull logExists)

  verifyOldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli migrate failure from compiled module prints recovery snapshot and guidance`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_migrate_recovery_guidance_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-2233445566778899.sqlite")

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  let expectedNewDbPath =
    Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)

  use setupOldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE fixture_student(name TEXT NOT NULL);"
    "INSERT INTO fixture_student(name) VALUES ('Alice');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupOldConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCliInDirectory (Some tempDir) [ "migrate"; "--assembly"; assemblyPath; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("migrate failed:", stdErr)
  Assert.Contains("Recovery snapshot:", stdErr)
  Assert.Contains("Old marker status: no marker", stdErr)
  Assert.Contains("Old _migration_log: absent", stdErr)
  Assert.Contains($"New database file: absent ({expectedNewDbPath})", stdErr)
  Assert.Contains("Recovery guidance:", stdErr)
  Assert.Contains("Run `mig plan` to confirm inferred paths and preflight status.", stdErr)

  Assert.False(File.Exists expectedNewDbPath)

  use verifyOldConn = openSqliteConnection oldDbPath

  use markerExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_marker' LIMIT 1",
      verifyOldConn
    )

  let markerExists = markerExistsCmd.ExecuteScalar()
  Assert.True(isNull markerExists)

  verifyOldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli drain cutover status and archive-old use compiled-module target discovery`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_operational_auto_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-a1b2c3d4e5f60718.sqlite")

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  let expectedArchivePath =
    Path.Combine(tempDir, "archive", Path.GetFileName oldDbPath)

  let expectedNewDbPath =
    Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)

  use setupOldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE fixture_student(id INTEGER NOT NULL);"
    "INSERT INTO fixture_student(id) VALUES (1);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupOldConn.Close()

  let migrateExitCode, migrateStdOut, migrateStdErr =
    runMigCliInDirectory (Some tempDir) [ "migrate"; "--assembly"; assemblyPath; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(0, migrateExitCode)
  Assert.True(String.IsNullOrWhiteSpace migrateStdErr, $"Expected no stderr output, got: {migrateStdErr}")
  Assert.Contains($"Old database: {oldDbPath}", migrateStdOut)
  Assert.Contains($"New database: {expectedNewDbPath}", migrateStdOut)

  let drainExitCode, drainStdOut, drainStdErr =
    runMigCliInDirectory (Some tempDir) [ "drain"; "--assembly"; assemblyPath; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(0, drainExitCode)
  Assert.True(String.IsNullOrWhiteSpace drainStdErr, $"Expected no stderr output, got: {drainStdErr}")
  Assert.Contains($"Old database: {oldDbPath}", drainStdOut)
  Assert.Contains($"New database: {expectedNewDbPath}", drainStdOut)

  let cutoverExitCode, cutoverStdOut, cutoverStdErr =
    runMigCliInDirectory (Some tempDir) [ "cutover"; "--assembly"; assemblyPath; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(0, cutoverExitCode)
  Assert.True(String.IsNullOrWhiteSpace cutoverStdErr, $"Expected no stderr output, got: {cutoverStdErr}")
  Assert.Contains($"New database: {expectedNewDbPath}", cutoverStdOut)
  Assert.Contains("Current migration status: ready", cutoverStdOut)

  let statusExitCode, statusStdOut, statusStdErr =
    runMigCliInDirectory (Some tempDir) [ "status"; "--assembly"; assemblyPath; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(0, statusExitCode)
  Assert.True(String.IsNullOrWhiteSpace statusStdErr, $"Expected no stderr output, got: {statusStdErr}")
  Assert.Contains($"Old database: {oldDbPath}", statusStdOut)
  Assert.Contains($"New database: {expectedNewDbPath}", statusStdOut)
  Assert.Contains("Migration status: ready", statusStdOut)

  let cleanupExitCode, cleanupStdOut, cleanupStdErr =
    runMigCliInDirectory
      (Some tempDir)
      [ "archive-old"
        "--assembly"
        assemblyPath
        "--module"
        "CompiledSchemaFixture" ]

  Assert.Equal(0, cleanupExitCode)
  Assert.True(String.IsNullOrWhiteSpace cleanupStdErr, $"Expected no stderr output, got: {cleanupStdErr}")
  Assert.Contains($"Old database: {oldDbPath}", cleanupStdOut)
  Assert.Contains($"Archived database: {expectedArchivePath}", cleanupStdOut)
  Assert.Contains("Replaced existing archive: no", cleanupStdOut)
  Assert.False(File.Exists oldDbPath)
  Assert.True(File.Exists expectedArchivePath)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli offline auto-discovers old db and archives after compiled-module copy`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_offline_auto_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath =
    Path.Combine(tempDir, "compiled-fixture-main-a1b2c3d4e5f60718.sqlite")

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  let expectedArchivePath =
    Path.Combine(tempDir, "archive", Path.GetFileName oldDbPath)

  let expectedNewDbPath =
    Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)

  use setupOldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE fixture_student(id INTEGER NOT NULL);"
    "INSERT INTO fixture_student(id) VALUES (1);"
    "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'draining');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupOldConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCliInDirectory (Some tempDir) [ "offline"; "--assembly"; assemblyPath; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains($"New database: {expectedNewDbPath}", stdOut)
  Assert.Contains("Previous old marker status: draining", stdOut)
  Assert.Contains($"Archived database: {expectedArchivePath}", stdOut)
  Assert.Contains("Replaced existing archive: no", stdOut)
  Assert.Contains("Hot-migration tables were not created.", stdOut)

  Assert.False(File.Exists oldDbPath)
  Assert.True(File.Exists expectedArchivePath)

  use verifyNewConn = openSqliteConnection expectedNewDbPath

  use newStatusExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_status' LIMIT 1",
      verifyNewConn
    )

  let newStatusExists = newStatusExistsCmd.ExecuteScalar()
  Assert.True(isNull newStatusExists)

  use studentCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM fixture_student", verifyNewConn)

  let studentCount = studentCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, studentCount)

  verifyNewConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli migrate skips when compiled-module database already exists`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_migrate_skip_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location

  let expectedDbPath = Path.Combine(Path.GetFullPath tempDir, compiledFixtureDbFile)

  use existingConn = openSqliteConnection expectedDbPath

  use initCmd =
    new SqliteCommand("CREATE TABLE sentinel(id INTEGER PRIMARY KEY, value TEXT NOT NULL);", existingConn)

  initCmd.ExecuteNonQuery() |> ignore
  existingConn.Close()

  let exitCode, stdOut, stdErr =
    runMigCliInDirectory (Some tempDir) [ "migrate"; "--assembly"; assemblyPath; "--module"; "CompiledSchemaFixture" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Migrate skipped.", stdOut)
  Assert.Contains($"Database already present for current schema: {expectedDbPath}", stdOut)

  use verifyConn = openSqliteConnection expectedDbPath

  use sentinelCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM sentinel", verifyConn)

  let sentinelCount = sentinelCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(0L, sentinelCount)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``migrate creates new database, copies rows, and sets recording markers`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_migrate_flow_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let newDbPath = Path.Combine(tempDir, "new.db")
  let schemaPath = Path.Combine(tempDir, "Schema.fs")

  use setupOldConn = openSqliteConnection oldDbPath

  [ "PRAGMA foreign_keys = ON;"
    "CREATE TABLE account(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "CREATE TABLE invoice(id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL, total REAL NOT NULL, FOREIGN KEY(account_id) REFERENCES account(id));"
    "INSERT INTO account(id, name) VALUES (10, 'Alice');"
    "INSERT INTO invoice(id, account_id, total) VALUES (100, 10, 42.5);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  File.WriteAllText(schemaPath, "module Schema\n")
  let expectedSchemaHash = deriveShortSchemaHashFromSourceFile schemaPath
  let schemaIdentity = loadSchemaIdentityFromSourceFile schemaPath
  let targetSchema = accountInvoiceSchema

  let migrateResult =
    runMigrateWithSchema oldDbPath schemaIdentity targetSchema newDbPath
    |> fun t -> t.Result

  match migrateResult with
  | Error ex -> failwith $"Expected migrate to succeed, got {ex.Message}"
  | Ok result ->
    Assert.Equal(newDbPath, result.newDbPath)
    Assert.Equal(2, result.copiedTables)
    Assert.Equal(2L, result.copiedRows)

  use verifyOldConn = openSqliteConnection oldDbPath

  use markerCmd =
    new SqliteCommand("SELECT status FROM _migration_marker WHERE id = 0", verifyOldConn)

  let markerStatus = markerCmd.ExecuteScalar() |> string
  Assert.Equal("recording", markerStatus)

  use oldLogCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM _migration_log", verifyOldConn)

  let oldLogCount = oldLogCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(0L, oldLogCount)

  use verifyNewConn = openSqliteConnection newDbPath

  use statusCmd =
    new SqliteCommand("SELECT status FROM _migration_status WHERE id = 0", verifyNewConn)

  let newStatus = statusCmd.ExecuteScalar() |> string
  Assert.Equal("migrating", newStatus)

  use accountCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM account", verifyNewConn)

  let accountCount = accountCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, accountCount)

  use invoiceCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM invoice", verifyNewConn)

  let invoiceCount = invoiceCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, invoiceCount)

  use mappingCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM _id_mapping", verifyNewConn)

  let mappingCount = mappingCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(2L, mappingCount)

  use progressCmd =
    new SqliteCommand(
      "SELECT last_replayed_log_id, drain_completed FROM _migration_progress WHERE id = 0",
      verifyNewConn
    )

  use progressReader = progressCmd.ExecuteReader()
  Assert.True(progressReader.Read())
  Assert.Equal(0L, progressReader.GetInt64(0))
  Assert.Equal(0L, progressReader.GetInt64(1))
  Assert.False(progressReader.Read())

  use schemaIdentityCmd =
    new SqliteCommand("SELECT schema_hash FROM _schema_identity WHERE id = 0", verifyNewConn)

  let storedSchemaHash = schemaIdentityCmd.ExecuteScalar() |> string
  Assert.Equal(expectedSchemaHash, storedSchemaHash)

  verifyOldConn.Close()
  verifyNewConn.Close()
  setupOldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``offline migrate creates new database without hot migration coordination tables`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_offline_flow_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let newDbPath = Path.Combine(tempDir, "new.db")
  let schemaPath = Path.Combine(tempDir, "Schema.fs")

  use setupOldConn = openSqliteConnection oldDbPath

  [ "PRAGMA foreign_keys = ON;"
    "CREATE TABLE account(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "CREATE TABLE invoice(id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL, total REAL NOT NULL, FOREIGN KEY(account_id) REFERENCES account(id));"
    "INSERT INTO account(id, name) VALUES (10, 'Alice');"
    "INSERT INTO invoice(id, account_id, total) VALUES (100, 10, 42.5);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  File.WriteAllText(schemaPath, "module Schema\n")
  let expectedSchemaHash = deriveShortSchemaHashFromSourceFile schemaPath
  let schemaIdentity = loadSchemaIdentityFromSourceFile schemaPath
  let targetSchema = accountInvoiceSchema

  let migrateResult =
    runOfflineMigrateWithSchema oldDbPath schemaIdentity targetSchema newDbPath
    |> fun t -> t.Result

  match migrateResult with
  | Error ex -> failwith $"Expected offline migrate to succeed, got {ex.Message}"
  | Ok result ->
    Assert.Equal(newDbPath, result.newDbPath)
    Assert.Equal(2, result.copiedTables)
    Assert.Equal(2L, result.copiedRows)

  use verifyOldConn = openSqliteConnection oldDbPath

  use oldMarkerExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_marker' LIMIT 1",
      verifyOldConn
    )

  let oldMarkerExists = oldMarkerExistsCmd.ExecuteScalar()
  Assert.True(isNull oldMarkerExists)

  use oldLogExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_log' LIMIT 1",
      verifyOldConn
    )

  let oldLogExists = oldLogExistsCmd.ExecuteScalar()
  Assert.True(isNull oldLogExists)

  use verifyNewConn = openSqliteConnection newDbPath

  use migrationStatusExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_status' LIMIT 1",
      verifyNewConn
    )

  let migrationStatusExists = migrationStatusExistsCmd.ExecuteScalar()
  Assert.True(isNull migrationStatusExists)

  use idMappingExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_id_mapping' LIMIT 1",
      verifyNewConn
    )

  let idMappingExists = idMappingExistsCmd.ExecuteScalar()
  Assert.True(isNull idMappingExists)

  use progressExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_progress' LIMIT 1",
      verifyNewConn
    )

  let progressExists = progressExistsCmd.ExecuteScalar()
  Assert.True(isNull progressExists)

  use accountCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM account", verifyNewConn)

  let accountCount = accountCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, accountCount)

  use invoiceCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM invoice", verifyNewConn)

  let invoiceCount = invoiceCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, invoiceCount)

  use schemaIdentityCmd =
    new SqliteCommand("SELECT schema_hash FROM _schema_identity WHERE id = 0", verifyNewConn)

  let storedSchemaHash = schemaIdentityCmd.ExecuteScalar() |> string
  Assert.Equal(expectedSchemaHash, storedSchemaHash)

  verifyOldConn.Close()
  verifyNewConn.Close()
  setupOldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``migrate preflight reports unsupported drift before creating migration side effects`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_migrate_preflight_drift_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let newDbPath = Path.Combine(tempDir, "new.db")
  let schemaPath = Path.Combine(tempDir, "Schema.fs")

  use setupOldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE student(id INTEGER NOT NULL, name TEXT NOT NULL);"
    "INSERT INTO student(id, name) VALUES (10, 'Alice');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  File.WriteAllText(schemaPath, "module Schema\n")
  let schemaIdentity = loadSchemaIdentityFromSourceFile schemaPath
  let targetSchema = studentSchema
  setupOldConn.Close()

  let migrateResult =
    runMigrateWithSchema oldDbPath schemaIdentity targetSchema newDbPath
    |> fun t -> t.Result

  match migrateResult with
  | Ok _ -> failwith "Expected migrate to fail during preflight drift validation."
  | Error ex ->
    Assert.Contains("Schema preflight report:", ex.Message)
    Assert.Contains("Supported differences:", ex.Message)
    Assert.Contains("Unsupported differences:", ex.Message)
    Assert.Contains("student.id", ex.Message)
    Assert.Contains("not copy-compatible", ex.Message)

  Assert.False(File.Exists newDbPath)

  use verifyOldConn = openSqliteConnection oldDbPath

  use markerExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_marker' LIMIT 1",
      verifyOldConn
    )

  let markerExists = markerExistsCmd.ExecuteScalar()
  Assert.True(isNull markerExists)

  use logExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_log' LIMIT 1",
      verifyOldConn
    )

  let logExists = logExistsCmd.ExecuteScalar()
  Assert.True(isNull logExists)

  verifyOldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``drain replays accumulated log entries and records replay checkpoint`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_drain_flow_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let newDbPath = Path.Combine(tempDir, "new.db")
  let schemaPath = Path.Combine(tempDir, "Schema.fs")

  use setupOldConn = openSqliteConnection oldDbPath

  [ "PRAGMA foreign_keys = ON;"
    "CREATE TABLE account(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "CREATE TABLE invoice(id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL, total REAL NOT NULL, FOREIGN KEY(account_id) REFERENCES account(id));"
    "INSERT INTO account(id, name) VALUES (10, 'Alice');"
    "INSERT INTO invoice(id, account_id, total) VALUES (100, 10, 42.5);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  File.WriteAllText(schemaPath, "module Schema\n")
  let schemaIdentity = loadSchemaIdentityFromSourceFile schemaPath
  let targetSchema = accountInvoiceSchema

  let migrateResult =
    runMigrateWithSchema oldDbPath schemaIdentity targetSchema newDbPath
    |> fun t -> t.Result

  match migrateResult with
  | Error ex -> failwith $"Expected migrate to succeed before drain test, got {ex.Message}"
  | Ok _ -> ()

  [ "INSERT INTO account(id, name) VALUES (11, 'Bob');"
    "INSERT INTO invoice(id, account_id, total) VALUES (101, 11, 15.0);"
    "UPDATE invoice SET total = 99.0 WHERE id = 100;"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 1, 'insert', 'account', '{\"id\":11,\"name\":\"Bob\"}');"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 2, 'insert', 'invoice', '{\"id\":101,\"account_id\":11,\"total\":15.0}');"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (2, 1, 'update', 'invoice', '{\"id\":100,\"account_id\":10,\"total\":99.0}');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  let drainResult = runDrain oldDbPath newDbPath |> fun t -> t.Result

  match drainResult with
  | Error ex -> failwith $"Expected drain to succeed, got {ex.Message}"
  | Ok result ->
    Assert.Equal(3, result.replayedEntries)
    Assert.Equal(0L, result.remainingEntries)

  use verifyOldConn = openSqliteConnection oldDbPath

  use markerCmd =
    new SqliteCommand("SELECT status FROM _migration_marker WHERE id = 0", verifyOldConn)

  let markerStatus = markerCmd.ExecuteScalar() |> string
  Assert.Equal("draining", markerStatus)

  use oldLogCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM _migration_log", verifyOldConn)

  let oldLogCount = oldLogCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(3L, oldLogCount)

  use verifyNewConn = openSqliteConnection newDbPath

  use accountCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM account", verifyNewConn)

  let accountCount = accountCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(2L, accountCount)

  use invoiceCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM invoice", verifyNewConn)

  let invoiceCount = invoiceCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(2L, invoiceCount)

  use summaryCmd =
    new SqliteCommand(
      "SELECT a.name, i.total FROM invoice i JOIN account a ON i.account_id = a.id ORDER BY a.name",
      verifyNewConn
    )

  use summaryReader = summaryCmd.ExecuteReader()
  Assert.True(summaryReader.Read())
  Assert.Equal("Alice", summaryReader.GetString(0))
  Assert.True(Math.Abs(summaryReader.GetDouble(1) - 99.0) < 0.0001)

  Assert.True(summaryReader.Read())
  Assert.Equal("Bob", summaryReader.GetString(0))
  Assert.True(Math.Abs(summaryReader.GetDouble(1) - 15.0) < 0.0001)
  Assert.False(summaryReader.Read())

  use progressCmd =
    new SqliteCommand(
      "SELECT last_replayed_log_id, drain_completed FROM _migration_progress WHERE id = 0",
      verifyNewConn
    )

  use progressReader = progressCmd.ExecuteReader()
  Assert.True(progressReader.Read())
  Assert.Equal(3L, progressReader.GetInt64(0))
  Assert.Equal(1L, progressReader.GetInt64(1))
  Assert.False(progressReader.Read())

  verifyOldConn.Close()
  verifyNewConn.Close()
  setupOldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``drain replay applies target triggers for replayed writes`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_drain_trigger_replay_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let newDbPath = Path.Combine(tempDir, "new.db")
  let schemaPath = Path.Combine(tempDir, "Schema.fs")

  use setupOldConn = openSqliteConnection oldDbPath

  [ "PRAGMA foreign_keys = ON;"
    "CREATE TABLE account(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "CREATE TABLE invoice(id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL, total REAL NOT NULL, FOREIGN KEY(account_id) REFERENCES account(id));"
    "INSERT INTO account(id, name) VALUES (10, 'Alice');"
    "INSERT INTO invoice(id, account_id, total) VALUES (100, 10, 42.5);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  File.WriteAllText(schemaPath, "module Schema\n")
  let schemaIdentity = loadSchemaIdentityFromSourceFile schemaPath
  let targetSchema = accountInvoiceSchema

  let migrateResult =
    runMigrateWithSchema oldDbPath schemaIdentity targetSchema newDbPath
    |> fun t -> t.Result

  match migrateResult with
  | Error ex -> failwith $"Expected migrate to succeed before trigger replay test, got {ex.Message}"
  | Ok _ -> ()

  use triggerSetupConn = openSqliteConnection newDbPath

  [ "CREATE TABLE invoice_replay_audit(id INTEGER PRIMARY KEY AUTOINCREMENT, invoice_id INTEGER NOT NULL, operation TEXT NOT NULL);"
    "CREATE TRIGGER trg_invoice_replay_audit AFTER INSERT ON invoice BEGIN INSERT INTO invoice_replay_audit(invoice_id, operation) VALUES (NEW.id, 'insert'); END;" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, triggerSetupConn)
    cmd.ExecuteNonQuery() |> ignore)

  triggerSetupConn.Close()

  [ "INSERT INTO account(id, name) VALUES (11, 'Bob');"
    "INSERT INTO invoice(id, account_id, total) VALUES (101, 11, 15.0);"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 1, 'insert', 'account', '{\"id\":11,\"name\":\"Bob\"}');"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 2, 'insert', 'invoice', '{\"id\":101,\"account_id\":11,\"total\":15.0}');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  let drainResult = runDrain oldDbPath newDbPath |> fun t -> t.Result

  match drainResult with
  | Error ex -> failwith $"Expected drain to succeed for trigger replay test, got {ex.Message}"
  | Ok result ->
    Assert.Equal(2, result.replayedEntries)
    Assert.Equal(0L, result.remainingEntries)

  use verifyNewConn = openSqliteConnection newDbPath

  use auditCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM invoice_replay_audit", verifyNewConn)

  let auditCount = auditCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, auditCount)

  use auditJoinCountCmd =
    new SqliteCommand(
      "SELECT COUNT(*) FROM invoice_replay_audit a JOIN invoice i ON i.id = a.invoice_id WHERE a.operation = 'insert'",
      verifyNewConn
    )

  let joinedAuditCount = auditJoinCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, joinedAuditCount)

  verifyNewConn.Close()
  setupOldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``post-cutover writes keep target triggers active`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cutover_trigger_writes_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let newDbPath = Path.Combine(tempDir, "new.db")
  let schemaPath = Path.Combine(tempDir, "Schema.fs")

  use setupOldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "INSERT INTO student(id, name) VALUES (1, 'Alice');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  File.WriteAllText(schemaPath, "module Schema\n")
  let schemaIdentity = loadSchemaIdentityFromSourceFile schemaPath
  let targetSchema = studentSchema

  let migrateResult =
    runMigrateWithSchema oldDbPath schemaIdentity targetSchema newDbPath
    |> fun t -> t.Result

  match migrateResult with
  | Error ex -> failwith $"Expected migrate to succeed before cutover trigger test, got {ex.Message}"
  | Ok _ -> ()

  use triggerSetupConn = openSqliteConnection newDbPath

  [ "CREATE TABLE student_write_audit(id INTEGER PRIMARY KEY AUTOINCREMENT, student_id INTEGER NOT NULL);"
    "CREATE TRIGGER trg_student_write_audit AFTER INSERT ON student BEGIN INSERT INTO student_write_audit(student_id) VALUES (NEW.id); END;" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, triggerSetupConn)
    cmd.ExecuteNonQuery() |> ignore)

  triggerSetupConn.Close()

  let drainResult = runDrain oldDbPath newDbPath |> fun t -> t.Result

  match drainResult with
  | Error ex -> failwith $"Expected drain to succeed before cutover trigger test, got {ex.Message}"
  | Ok _ -> ()

  let cutoverResult = runCutover newDbPath |> fun t -> t.Result

  match cutoverResult with
  | Error ex -> failwith $"Expected cutover to succeed before trigger write test, got {ex.Message}"
  | Ok result -> Assert.Equal("migrating", result.previousStatus)

  let writeResult =
    dbTxn newDbPath {
      let! _ =
        fun tx ->
          task {
            MigrationLog.ensureWriteAllowed tx

            use cmd =
              new SqliteCommand("INSERT INTO student(name) VALUES (@name)", tx.Connection, tx)

            cmd.Parameters.AddWithValue("@name", "Bob") |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return Ok()
          }

      return ()
    }
    |> fun t -> t.Result

  match writeResult with
  | Error ex -> failwith $"Expected write after cutover to succeed, got {ex.Message}"
  | Ok _ -> ()

  use verifyNewConn = openSqliteConnection newDbPath

  use auditCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM student_write_audit", verifyNewConn)

  let auditCount = auditCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, auditCount)

  use auditedNameCmd =
    new SqliteCommand(
      "SELECT s.name FROM student_write_audit a JOIN student s ON s.id = a.student_id ORDER BY a.id",
      verifyNewConn
    )

  let auditedName = auditedNameCmd.ExecuteScalar() |> string
  Assert.Equal("Bob", auditedName)

  verifyNewConn.Close()
  setupOldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``schema reflection maps DU optional cases into extension tables`` () =
  let types = [ typeof<ReflectionStudent>; typeof<ReflectionStudentOpt> ]

  match buildSchemaFromTypes types with
  | Error e -> failwith $"reflection failed: {e}"
  | Ok schema ->
    let extension =
      schema.tables
      |> List.find (fun table -> table.name = "reflection_student_email")

    let studentId =
      extension.columns
      |> List.find (fun column -> column.name = "reflection_student_id")

    let hasPk =
      studentId.constraints
      |> List.exists (function
        | PrimaryKey _ -> true
        | _ -> false)

    Assert.True hasPk

    let hasFk =
      studentId.constraints
      |> List.exists (function
        | ForeignKey fk -> fk.refTable = "reflection_student"
        | _ -> false)

    Assert.True hasFk
    Assert.True(extension.columns |> List.exists (fun column -> column.name = "email"))

[<Fact>]
let ``schema reflection maps ViewSql views`` () =
  let types = [ typeof<ReflectionStudentView> ]

  match buildSchemaFromTypes types with
  | Error e -> failwith $"reflection failed: {e}"
  | Ok schema ->
    let view =
      schema.views |> List.find (fun item -> item.name = "reflection_student_view")

    Assert.Equal("CREATE VIEW reflection_student_view AS SELECT id, name FROM reflection_student", view.sql)
    Assert.True(view.queryByAnnotations |> List.exists (fun q -> q.columns = [ "name" ]))

[<Fact>]
let ``schema reflection synthesizes SQL for View with Join attributes`` () =
  let types =
    [ typeof<JoinStudent>
      typeof<JoinCourse>
      typeof<JoinCourseGrade>
      typeof<JoinStudentCourseGrade> ]

  match buildSchemaFromTypes types with
  | Error e -> failwith $"reflection failed: {e}"
  | Ok schema ->
    let view =
      schema.views |> List.find (fun item -> item.name = "join_student_course_grade")

    let sql = view.sql

    Assert.Contains("CREATE VIEW join_student_course_grade AS", sql)
    Assert.Contains("FROM join_course jc", sql)
    Assert.Contains("JOIN join_student js ON jc.student_id = js.id", sql)
    Assert.Contains("JOIN join_course_grade jcg ON jcg.course_id = jc.id", sql)
    Assert.Contains("js.id AS student_id", sql)
    Assert.Contains("js.name AS student_name", sql)
    Assert.Contains("jc.title AS title", sql)
    Assert.Contains("jcg.grade AS grade", sql)

[<Fact>]
let ``schema reflection synthesizes SQL for View with composite foreign-key joins`` () =
  let types =
    [ typeof<ReflectionCompositeUser>
      typeof<ReflectionCompositeWallet>
      typeof<ReflectionCompositeWalletView> ]

  match buildSchemaFromTypes types with
  | Error e -> failwith $"reflection failed: {e}"
  | Ok schema ->
    let view =
      schema.views
      |> List.find (fun item -> item.name = "reflection_composite_wallet_view")

    let sql = view.sql

    Assert.Contains(
      "JOIN reflection_composite_user rcu ON rcw.user_tenant_id = rcu.tenant_id AND rcw.user_user_id = rcu.user_id",
      sql
    )

    Assert.Contains("rcu.tenant_id AS user_tenant_id", sql)
    Assert.Contains("rcu.user_id AS user_user_id", sql)
    Assert.Contains("rcu.name AS user_name", sql)
    Assert.Contains("rcw.address AS address", sql)

[<Fact>]
let ``codegen generates module and query methods from schema model`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_codegen_{Guid.NewGuid()}")
  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "Students.fs")

  let studentTable =
    { name = "student"
      previousName = None
      dropColumns = []
      columns =
        [ { name = "id"
            previousName = None
            columnType = SqlInteger
            constraints =
              [ PrimaryKey
                  { constraintName = None
                    columns = []
                    isAutoincrement = true } ]
            enumLikeDu = None
            unitOfMeasure = None }
          { name = "name"
            previousName = None
            columnType = SqlText
            constraints = [ NotNull ]
            enumLikeDu = None
            unitOfMeasure = None }
          { name = "age"
            previousName = None
            columnType = SqlInteger
            constraints = [ NotNull ]
            enumLikeDu = None
            unitOfMeasure = None } ]
      constraints = []
      queryByAnnotations = [ { columns = [ "name"; "age" ] } ]
      queryLikeAnnotations = [ { columns = [ "name" ] } ]
      queryByOrCreateAnnotations = [ { columns = [ "name"; "age" ] } ]
      selectOneAnnotations = []
      insertOrIgnoreAnnotations = [ InsertOrIgnoreAnnotation ]
      deleteAllAnnotations = [ DeleteAllAnnotation ]
      upsertAnnotations = [ UpsertAnnotation ] }

  let schema =
    { emptyFile with
        tables = [ studentTable ] }

  match generateCodeFromModel "Students" schema outputPath with
  | Error e -> failwith $"codegen failed: {e}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath
    Assert.Contains("module Students", generated)
    Assert.Contains("open MigLib.Db", generated)
    Assert.Contains("static member Insert (item: Student) (tx: SqliteTransaction)", generated)
    Assert.Contains("static member DeleteAll", generated)
    Assert.Contains("static member Upsert (item: Student) (tx: SqliteTransaction)", generated)
    Assert.Contains("static member SelectAll", generated)
    Assert.Contains("static member SelectByNameAge", generated)
    Assert.Contains("(name: string, age: int64)", generated)
    Assert.Contains("(tx: SqliteTransaction)", generated)
    Assert.Contains("executeWrite", generated)
    Assert.Contains("MigrationLog.recordInsert", generated)
    Assert.Contains("MigrationLog.recordUpdate", generated)
    Assert.Contains("MigrationLog.recordDelete", generated)
    Assert.Contains("return Ok newId", generated)
    Assert.DoesNotContain("returnOk", generated)
    Assert.DoesNotContain("returnError", generated)
    Assert.DoesNotContain(": Result<", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``querybyorcreate for regular tables re-queries by annotation columns`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_querybyorcreate_regular_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "SlugArticle.fs")

  match generateCodeFromTypes "SlugArticleQueries" [ typeof<SlugArticle> ] outputPath with
  | Error e -> failwith $"codegen-from-types failed: {e}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath
    Assert.Contains("static member SelectBySlugOrInsert", generated)
    Assert.Contains("(newItem: SlugArticle)", generated)
    Assert.Contains("(tx: SqliteTransaction)", generated)
    Assert.Contains("SELECT slug, title FROM slug_article WHERE slug = @slug LIMIT 1", generated)
    Assert.DoesNotContain("let! getResult = SlugArticle.SelectById", generated)
    Assert.Contains("querySingleOrInsert select", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``querybyorcreate for normalized tables uses base PK column in re-query joins`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_querybyorcreate_normalized_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "Product.fs")

  match generateCodeFromTypes "ProductQueries" [ typeof<Product>; typeof<ProductOpt> ] outputPath with
  | Error e -> failwith $"codegen-from-types failed: {e}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath
    Assert.Contains("static member SelectByNameOrInsert", generated)
    Assert.Contains("(newItem: NewProduct)", generated)
    Assert.Contains("LEFT JOIN product_stock estock ON b.code = estock.product_id", generated)
    Assert.Contains("LEFT JOIN product_stock ext0 ON product.code = ext0.product_id", generated)
    Assert.DoesNotContain("LEFT JOIN product_stock ext0 ON product.id = ext0.product_id", generated)
    Assert.DoesNotContain("cmd2.Parameters.AddWithValue(\"@product_id\", productId)", generated)
    Assert.DoesNotContain("let! getResult = Product.SelectById newId tx", generated)
    Assert.Contains("querySingleOrInsert select", generated)
    Assert.DoesNotContain(": Result<", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``codegen from reflected types uses composite primary keys in CRUD methods`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_reflection_composite_pk_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "ReflectionCompositeUser.fs")

  match generateCodeFromTypes "ReflectionCompositeUserQueries" [ typeof<ReflectionCompositeUser> ] outputPath with
  | Error error -> failwith $"codegen-from-types failed: {error}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath

    Assert.Contains(
      "SELECT tenant_id, user_id, name FROM reflection_composite_user WHERE tenant_id = @tenant_id AND user_id = @user_id",
      generated
    )

    Assert.Contains(
      "UPDATE reflection_composite_user SET name = @name WHERE tenant_id = @tenant_id AND user_id = @user_id",
      generated
    )

    Assert.Contains(
      "DELETE FROM reflection_composite_user WHERE tenant_id = @tenant_id AND user_id = @user_id",
      generated
    )

    Assert.Contains("ReflectionCompositeUser.SelectById item.TenantId item.UserId tx", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``codegen from reflected normalized types uses composite extension joins`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_reflection_normalized_composite_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "CompositeProduct.fs")

  match
    generateCodeFromTypes "CompositeProductQueries" [ typeof<CompositeProduct>; typeof<CompositeProductOpt> ] outputPath
  with
  | Error error -> failwith $"codegen-from-types failed: {error}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath

    Assert.Contains(
      "LEFT JOIN composite_product_stock ext0 ON composite_product.tenant_id = ext0.composite_product_tenant_id AND composite_product.code = ext0.composite_product_code",
      generated
    )

    Assert.Contains(
      "INSERT INTO composite_product_stock (composite_product_tenant_id, composite_product_code, stock) VALUES (@composite_product_tenant_id, @composite_product_code, @stock)",
      generated
    )

  Directory.Delete(tempDir, true)

[<Fact>]
let ``schema reflection rejects type with both AutoIncPK and PK`` () =
  match buildSchemaFromTypes [ typeof<ConflictingPkStudent> ] with
  | Ok _ -> failwith "Expected reflection failure for conflicting PK attributes"
  | Error error ->
    Assert.Contains("has both AutoIncPK and PK attributes", error)
    Assert.Contains("ConflictingPkStudent", error)

[<Fact>]
let ``schema reflection rejects record fields referencing types outside schema`` () =
  match buildSchemaFromTypes [ typeof<WalletWithOutsideRef> ] with
  | Ok _ -> failwith "Expected reflection failure for outside-schema reference"
  | Error error ->
    Assert.Contains("WalletWithOutsideRef.account", error)
    Assert.Contains("outside schema", error)

[<Fact>]
let ``schema reflection rejects foreign-key references to types without primary keys`` () =
  match buildSchemaFromTypes [ typeof<ParentWithoutPk>; typeof<ChildWithParentWithoutPk> ] with
  | Ok _ -> failwith "Expected reflection failure for FK target without PK"
  | Error error ->
    Assert.Contains("ChildWithParentWithoutPk.parent", error)
    Assert.Contains("does not declare PK or AutoIncPK", error)

[<Fact>]
let ``schema reflection rejects disconnected view join chains`` () =
  let types =
    [ typeof<JoinChainA>
      typeof<JoinChainB>
      typeof<JoinChainC>
      typeof<JoinChainD>
      typeof<DisconnectedJoinView> ]

  match buildSchemaFromTypes types with
  | Ok _ -> failwith "Expected reflection failure for disconnected Join chain"
  | Error error ->
    Assert.Contains("Join chain is disconnected", error)
    Assert.Contains("join_chain_c", error)

[<Fact>]
let ``schema reflection maps enum-like DU columns as text with metadata`` () =
  match buildSchemaFromTypes [ typeof<ReflectionStatusStudent> ] with
  | Error error -> failwith $"Expected enum-like DU reflection to succeed, got: {error}"
  | Ok schema ->
    let table =
      schema.tables
      |> List.find (fun candidate -> candidate.name = "reflection_status_student")

    let statusColumn = table.columns |> List.find (fun column -> column.name = "status")

    Assert.Equal(SqlText, statusColumn.columnType)
    Assert.True(statusColumn.enumLikeDu.IsSome)
    Assert.Equal("ReflectionStatus", statusColumn.enumLikeDu.Value.typeName)
    Assert.Equal<string list>([ "Active"; "InProgress" ], statusColumn.enumLikeDu.Value.cases)

[<Fact>]
let ``schema reflection rejects payload unions as scalar columns`` () =
  match buildSchemaFromTypes [ typeof<UnsupportedPayloadStatusStudent> ] with
  | Ok _ -> failwith "Expected reflection failure for payload DU scalar column"
  | Error error ->
    Assert.Contains("UnsupportedPayloadStatusStudent.status", error)
    Assert.Contains("unsupported type", error, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``querybyorinsert works for composite primary keys without SelectById fallback`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_querybyorcreate_composite_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "OrderItem.fs")

  let orderItemTable =
    { name = "order_item"
      previousName = None
      dropColumns = []
      columns =
        [ { name = "order_id"
            previousName = None
            columnType = SqlInteger
            constraints = [ NotNull ]
            enumLikeDu = None
            unitOfMeasure = None }
          { name = "sku"
            previousName = None
            columnType = SqlText
            constraints = [ NotNull ]
            enumLikeDu = None
            unitOfMeasure = None }
          { name = "description"
            previousName = None
            columnType = SqlText
            constraints = [ NotNull ]
            enumLikeDu = None
            unitOfMeasure = None } ]
      constraints =
        [ PrimaryKey
            { constraintName = None
              columns = [ "order_id"; "sku" ]
              isAutoincrement = false } ]
      queryByAnnotations = []
      queryLikeAnnotations = []
      queryByOrCreateAnnotations = [ { columns = [ "description" ] } ]
      selectOneAnnotations = []
      insertOrIgnoreAnnotations = []
      deleteAllAnnotations = []
      upsertAnnotations = [] }

  let schema =
    { emptyFile with
        tables = [ orderItemTable ] }

  match generateCodeFromModel "OrderItemQueries" schema outputPath with
  | Error error -> failwith $"codegen failed: {error}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath
    Assert.Contains("static member SelectByDescriptionOrInsert", generated)
    Assert.Contains("(newItem: OrderItem)", generated)
    Assert.Contains("(tx: SqliteTransaction)", generated)

    Assert.Contains(
      "SELECT order_id, sku, description FROM order_item WHERE description = @description LIMIT 1",
      generated
    )

    Assert.DoesNotContain("let! getResult = OrderItem.SelectById", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``codegen rejects upsert annotation when table has no primary key`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_upsert_nopk_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "NoPk.fs")

  let table =
    { name = "no_pk"
      previousName = None
      dropColumns = []
      columns =
        [ { name = "name"
            previousName = None
            columnType = SqlText
            constraints = [ NotNull ]
            enumLikeDu = None
            unitOfMeasure = None } ]
      constraints = []
      queryByAnnotations = []
      queryLikeAnnotations = []
      queryByOrCreateAnnotations = []
      selectOneAnnotations = []
      insertOrIgnoreAnnotations = []
      deleteAllAnnotations = []
      upsertAnnotations = [ UpsertAnnotation ] }

  let schema = { emptyFile with tables = [ table ] }

  match generateCodeFromModel "NoPkQueries" schema outputPath with
  | Ok _ -> failwith "Expected codegen failure when Upsert is used on a table without primary key"
  | Error error ->
    Assert.Contains("Upsert annotation requires a primary key", error)
    Assert.Contains("no_pk", error)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``codegen allows deleteall annotation when table has no primary key`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_deleteall_nopk_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "NoPkDeleteAll.fs")

  let table =
    { name = "no_pk"
      previousName = None
      dropColumns = []
      columns =
        [ { name = "name"
            previousName = None
            columnType = SqlText
            constraints = [ NotNull ]
            enumLikeDu = None
            unitOfMeasure = None } ]
      constraints = []
      queryByAnnotations = []
      queryLikeAnnotations = []
      queryByOrCreateAnnotations = []
      selectOneAnnotations = []
      insertOrIgnoreAnnotations = []
      deleteAllAnnotations = [ DeleteAllAnnotation ]
      upsertAnnotations = [] }

  let schema = { emptyFile with tables = [ table ] }

  match generateCodeFromModel "NoPkDeleteAllQueries" schema outputPath with
  | Error error -> failwith $"Expected codegen success for DeleteAll without primary key, got: {error}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath
    Assert.Contains("static member DeleteAll", generated)
    Assert.Contains("DELETE FROM no_pk", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``codegen rejects upsert annotation on views`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_upsert_view_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "ViewWithUpsert.fs")

  let studentTable =
    { name = "student"
      previousName = None
      dropColumns = []
      columns =
        [ { name = "id"
            previousName = None
            columnType = SqlInteger
            constraints =
              [ PrimaryKey
                  { constraintName = None
                    columns = []
                    isAutoincrement = true } ]
            enumLikeDu = None
            unitOfMeasure = None }
          { name = "name"
            previousName = None
            columnType = SqlText
            constraints = [ NotNull ]
            enumLikeDu = None
            unitOfMeasure = None } ]
      constraints = []
      queryByAnnotations = []
      queryLikeAnnotations = []
      queryByOrCreateAnnotations = []
      selectOneAnnotations = []
      insertOrIgnoreAnnotations = []
      deleteAllAnnotations = []
      upsertAnnotations = [] }

  let view =
    { name = "student_view"
      previousName = None
      sql = "SELECT id, name FROM student"
      declaredColumns = []
      dependencies = [ "student" ]
      queryByAnnotations = []
      queryLikeAnnotations = []
      queryByOrCreateAnnotations = []
      selectOneAnnotations = []
      insertOrIgnoreAnnotations = []
      deleteAllAnnotations = []
      upsertAnnotations = [ UpsertAnnotation ] }

  let schema =
    { emptyFile with
        tables = [ studentTable ]
        views = [ view ] }

  match generateCodeFromModel "ViewWithUpsertQueries" schema outputPath with
  | Ok _ -> failwith "Expected codegen failure when Upsert is used on a view"
  | Error error ->
    Assert.Contains("Upsert annotation is not supported on views", error)
    Assert.Contains("student_view", error)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``codegen rejects deleteall annotation on views`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_deleteall_view_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "ViewWithDeleteAll.fs")

  let studentTable =
    { name = "student"
      previousName = None
      dropColumns = []
      columns =
        [ { name = "id"
            previousName = None
            columnType = SqlInteger
            constraints =
              [ PrimaryKey
                  { constraintName = None
                    columns = []
                    isAutoincrement = true } ]
            enumLikeDu = None
            unitOfMeasure = None }
          { name = "name"
            previousName = None
            columnType = SqlText
            constraints = [ NotNull ]
            enumLikeDu = None
            unitOfMeasure = None } ]
      constraints = []
      queryByAnnotations = []
      queryLikeAnnotations = []
      queryByOrCreateAnnotations = []
      selectOneAnnotations = []
      insertOrIgnoreAnnotations = []
      deleteAllAnnotations = []
      upsertAnnotations = [] }

  let view =
    { name = "student_view"
      previousName = None
      sql = "SELECT id, name FROM student"
      declaredColumns = []
      dependencies = [ "student" ]
      queryByAnnotations = []
      queryLikeAnnotations = []
      queryByOrCreateAnnotations = []
      selectOneAnnotations = []
      insertOrIgnoreAnnotations = []
      deleteAllAnnotations = [ DeleteAllAnnotation ]
      upsertAnnotations = [] }

  let schema =
    { emptyFile with
        tables = [ studentTable ]
        views = [ view ] }

  match generateCodeFromModel "ViewWithDeleteAllQueries" schema outputPath with
  | Ok _ -> failwith "Expected codegen failure when DeleteAll is used on a view"
  | Error error ->
    Assert.Contains("DeleteAll annotation is not supported on views", error)
    Assert.Contains("student_view", error)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``codegen writes only the requested source file`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_single_output_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "Students.fs")

  match generateCodeFromTypes "Students" [ typeof<ReflectionStudent> ] outputPath with
  | Error error -> failwith $"codegen-from-types failed: {error}"
  | Ok stats ->
    Assert.Equal<string list>([ outputPath ], stats.GeneratedFiles)
    Assert.True(File.Exists outputPath, $"Expected generated file at: {outputPath}")
    Assert.False(File.Exists(Path.Combine(tempDir, "Students.fsproj")))

  Directory.Delete(tempDir, true)

[<Fact>]
let ``codegen can run directly from reflected types`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_types_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "ReflectionStudent.fs")

  match generateCodeFromTypes "ReflectionStudent" [ typeof<ReflectionStudent> ] outputPath with
  | Error e -> failwith $"codegen-from-types failed: {e}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath
    Assert.Contains("module ReflectionStudent", generated)
    Assert.Contains("let Schema:", generated)
    Assert.DoesNotContain("let SchemaHash =", generated)
    Assert.Contains("static member SelectByNameAge", generated)
    Assert.Contains("(name: string, age: int64)", generated)
    Assert.Contains("(tx: SqliteTransaction)", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``build api derives schema-bound db file name from Schema fs path`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_build_dbfile_{Guid.NewGuid()}")
  Directory.CreateDirectory tempDir |> ignore

  let schemaPath = Path.Combine(tempDir, "Schema.fs")

  File.WriteAllText(
    schemaPath,
    "module Schema\n\nopen MigLib.Db\n\n[<AutoIncPK \"id\">]\ntype Student = { id: int64; name: string }\n"
  )

  match deriveSchemaBoundDbFileName "my-app" None schemaPath with
  | Error error -> failwith $"deriveSchemaBoundDbFileName failed: {error}"
  | Ok dbFileName ->
    let expectedDbFile =
      $"my-app-main-{deriveShortSchemaHashFromSourceFile schemaPath}.sqlite"

    Assert.Equal(expectedDbFile, dbFileName)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``build api generates db code from reflected types without sibling project file`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_build_codegen_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let schemaPath = Path.Combine(tempDir, "Schema.fs")
  let outputPath = Path.Combine(tempDir, "Db.fs")
  let projectPath = Path.Combine(tempDir, "Db.fsproj")

  File.WriteAllText(
    schemaPath,
    "module Schema\n\nopen MigLib.Db\n\n[<AutoIncPK \"id\">]\ntype ReflectionStudent = { id: int64; name: string; age: int64 }\n"
  )

  match generateDbCodeFromTypes "Db" "students-app" schemaPath [ typeof<ReflectionStudent> ] outputPath with
  | Error error -> failwith $"generateDbCodeFromTypes failed: {error}"
  | Ok stats ->
    let generated = File.ReadAllText outputPath

    Assert.Equal<string list>([ outputPath ], stats.GeneratedFiles)
    Assert.Contains("module Db", generated)
    Assert.Contains("let DbApp =", generated)
    Assert.Contains("let DefaultDbInstance =", generated)
    Assert.Contains("let DbFileForInstance", generated)
    Assert.Contains("let SchemaHash =", generated)
    Assert.Contains("let SchemaIdentity:", generated)
    Assert.Contains("let Schema:", generated)
    Assert.Contains("static member SelectByNameAge", generated)
    Assert.False(File.Exists projectPath, "Embedded Db.fs generation should not emit a sibling .fsproj file.")

  Directory.Delete(tempDir, true)

[<Fact>]
let ``compiled schema loader reads generated schema values from assembly module`` () =
  let assembly = typeof<CompiledSchemaFixture.Marker>.Assembly

  match tryLoadGeneratedSchemaModuleFromAssembly assembly "CompiledSchemaFixture" with
  | Error error -> failwith $"Expected compiled schema load to succeed, got: {error}"
  | Ok loaded ->
    Assert.Equal(Some CompiledSchemaFixture.DbApp, loaded.dbApp)
    Assert.Equal(Some CompiledSchemaFixture.DefaultDbInstance, loaded.defaultDbInstance)
    Assert.Equal(Some "0123456789abcdef", loaded.schemaHash)
    Assert.True(loaded.schemaIdentity.IsSome)
    Assert.Equal("0123456789abcdef", loaded.schemaIdentity.Value.schemaHash)
    Assert.Equal(Some "fixture-commit", loaded.schemaIdentity.Value.schemaCommit)
    Assert.Single loaded.schema.tables |> ignore
    Assert.Equal("fixture_student", loaded.schema.tables.Head.name)

[<Fact>]
let ``compiled schema loader reports missing module clearly`` () =
  let assembly = typeof<CompiledSchemaFixture.Marker>.Assembly

  match tryLoadGeneratedSchemaModuleFromAssembly assembly "DoesNotExist" with
  | Ok _ -> failwith "Expected compiled schema load to fail for a missing module"
  | Error error -> Assert.Contains("Compiled module 'DoesNotExist' was not found", error)

[<Fact>]
let ``compiled schema init wrapper creates database from module schema`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_compiled_schema_init_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "compiled-init.sqlite")
  let assembly = typeof<CompiledSchemaFixture.Marker>.Assembly

  let initResult =
    runInitFromAssembly assembly "CompiledSchemaFixture" dbPath |> fun t -> t.Result

  match initResult with
  | Error ex -> failwith $"Expected compiled schema init to succeed, got: {ex.Message}"
  | Ok result ->
    Assert.Equal(dbPath, result.newDbPath)
    Assert.Equal(1L, result.seededRows)

  use verifyConn = openSqliteConnection dbPath

  use tableExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fixture_student' LIMIT 1",
      verifyConn
    )

  let tableExists = tableExistsCmd.ExecuteScalar()
  Assert.False(isNull tableExists)

  use seededRowsCmd =
    new SqliteCommand("SELECT COUNT(*) FROM fixture_student", verifyConn)

  let seededRows = seededRowsCmd.ExecuteScalar() :?> int64
  Assert.Equal(1L, seededRows)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``build init wrapper creates schema-bound database and skips rerun`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_build_init_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let assemblyPath = typeof<CompiledSchemaFixture.Marker>.Assembly.Location
  let expectedDbPath = Path.Combine(tempDir, compiledFixtureDbFile)

  let firstRun =
    initDbFromAssemblyModulePath tempDir None assemblyPath "CompiledSchemaFixture"
    |> fun t -> t.Result

  match firstRun with
  | Error error -> failwith $"Expected build init wrapper to succeed, got: {error}"
  | Ok result ->
    Assert.Equal(expectedDbPath, result.newDbPath)
    Assert.Equal(1L, result.seededRows)
    Assert.False(result.skipped)

  let secondRun =
    initDbFromAssemblyModulePath tempDir None assemblyPath "CompiledSchemaFixture"
    |> fun t -> t.Result

  match secondRun with
  | Error error -> failwith $"Expected build init wrapper rerun to skip cleanly, got: {error}"
  | Ok result ->
    Assert.Equal(expectedDbPath, result.newDbPath)
    Assert.Equal(0L, result.seededRows)
    Assert.True(result.skipped)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``compiled schema migrate wrapper copies rows using module schema identity`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_compiled_schema_migrate_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.sqlite")
  let newDbPath = Path.Combine(tempDir, "new.sqlite")
  let assembly = typeof<CompiledSchemaFixture.Marker>.Assembly

  use oldConn = openSqliteConnection oldDbPath

  [ "CREATE TABLE fixture_student(id INTEGER NOT NULL);"
    "INSERT INTO fixture_student(id) VALUES (1);"
    "INSERT INTO fixture_student(id) VALUES (2);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  oldConn.Close()

  let migrateResult =
    runMigrateFromAssembly assembly "CompiledSchemaFixture" oldDbPath newDbPath
    |> fun t -> t.Result

  match migrateResult with
  | Error ex -> failwith $"Expected compiled schema migrate to succeed, got: {ex.Message}"
  | Ok result ->
    Assert.Equal(newDbPath, result.newDbPath)
    Assert.Equal(1, result.copiedTables)
    Assert.Equal(2L, result.copiedRows)

  use verifyNewConn = openSqliteConnection newDbPath

  use rowCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM fixture_student", verifyNewConn)

  let rowCount = rowCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(2L, rowCount)

  use schemaIdentityCmd =
    new SqliteCommand("SELECT schema_hash FROM _schema_identity WHERE id = 0", verifyNewConn)

  let schemaHash = schemaIdentityCmd.ExecuteScalar() |> string
  Assert.Equal("0123456789abcdef", schemaHash)

  verifyNewConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``codegen can run directly from in-memory schema model`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_model_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "Generated.fs")
  let projectPath = Path.Combine(tempDir, "Generated.fsproj")

  let schema =
    { emptyFile with
        tables =
          [ { mkTable
                "student"
                [ mkColumn
                    "id"
                    SqlInteger
                    [ NotNull
                      PrimaryKey
                        { constraintName = None
                          columns = []
                          isAutoincrement = true } ]
                  mkColumn "name" SqlText [ NotNull ] ]
                [] with
                queryByAnnotations = [ { columns = [ "name" ] } ] } ] }

  match generateCodeFromModel "Generated" schema outputPath with
  | Error e -> failwith $"codegen-from-model failed: {e}"
  | Ok stats ->
    let generated = File.ReadAllText outputPath

    Assert.Equal<string list>([ outputPath ], stats.GeneratedFiles)
    Assert.Contains("module Generated", generated)
    Assert.DoesNotContain("let DbApp =", generated)
    Assert.DoesNotContain("let DbFileForInstance", generated)
    Assert.Contains("static member SelectByName (name: string) (tx: SqliteTransaction)", generated)
    Assert.False(File.Exists projectPath, "Code generation should not emit a sibling .fsproj file.")

  Directory.Delete(tempDir, true)

[<Fact>]
let ``compiled schema reflection derives inserts from module-level record lets`` () =
  let assembly = typeof<SchemaReflectionFixture.Student>.Assembly

  match buildSchemaFromAssemblyModule assembly "SchemaReflectionFixture" with
  | Error error -> failwith $"Expected schema reflection from assembly module to succeed, got: {error}"
  | Ok schema ->
    let insert =
      schema.inserts
      |> List.find (fun candidate -> candidate.table = "seeded_student")

    Assert.Equal<string list>([ "id"; "name" ], insert.columns)

    Assert.Equal<Expr list list>([ [ Integer 1; String "Alice" ]; [ Integer 2; String "Bob" ] ], insert.values)

[<Fact>]
let ``compiled schema reflection expands composite foreign keys in derived seed inserts`` () =
  let assembly = typeof<SchemaReflectionFixture.Student>.Assembly

  match buildSchemaFromAssemblyModule assembly "SchemaReflectionFixture" with
  | Error error -> failwith $"Expected schema reflection from assembly module to succeed, got: {error}"
  | Ok schema ->
    let insert =
      schema.inserts
      |> List.find (fun candidate -> candidate.table = "seeded_tenant_session")

    Assert.Equal<string list>([ "id"; "user_tenant_id"; "user_external_id"; "token" ], insert.columns)

    Assert.Equal<Expr list list>(
      [ [ Integer 10; String "tenant-a"; String "user-1"; String "session-1" ] ],
      insert.values
    )

[<Fact>]
let ``codegen from assembly module emits inserts derived from module-level record lets`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_seed_reflection_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "Generated.fs")
  let schemaPath = Path.Combine(__SOURCE_DIRECTORY__, "SchemaReflectionFixture.fs")
  let assembly = typeof<SchemaReflectionFixture.Student>.Assembly

  match
    generateCodeFromAssemblyModuleWithDbFile
      "Generated"
      "seed-reflection"
      schemaPath
      assembly
      "SchemaReflectionFixture"
      outputPath
  with
  | Error error -> failwith $"Expected codegen from assembly module to succeed, got: {error}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath

    Assert.Contains("inserts =", generated)
    Assert.Contains("table = \"seeded_student\"", generated)
    Assert.Contains("Expr.Integer 1", generated)
    Assert.Contains("Expr.String \"Alice\"", generated)
    Assert.Contains("Expr.Integer 2", generated)
    Assert.Contains("Expr.String \"Bob\"", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``codegen emits enum-like DU types and uses them in table and view APIs`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_enum_reflection_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "Generated.fs")

  let schema =
    match
      buildSchemaFromTypes
        [ typeof<SchemaReflectionFixture.StudentWithStatus>
          typeof<SchemaReflectionFixture.StudentStatusView> ]
    with
    | Ok value -> value
    | Error e -> failwith $"schema-from-types failed: {e}"

  match generateCodeFromModel "Generated" schema outputPath with
  | Error e -> failwith $"codegen-from-model failed: {e}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath
    Assert.Contains("type Status =", generated)
    Assert.Contains("| Active", generated)
    Assert.Contains("| InProgress", generated)
    Assert.Contains("Status: Status", generated)
    Assert.Contains("static member SelectByStatus", generated)
    Assert.Contains("(status: Status)", generated)
    Assert.Contains("type StudentStatusView =", generated)
    Assert.Contains("Status: Status", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``compiled schema reflection does not preserve unit-of-measure metadata`` () =
  match buildSchemaFromTypes [ typeof<SchemaReflectionFixture.File> ] with
  | Error e -> failwith $"schema-from-types failed: {e}"
  | Ok schema ->
    Assert.Empty schema.measureTypes

    let fileTable = schema.tables |> List.find (fun table -> table.name = "file")

    let contentLength =
      fileTable.columns |> List.find (fun column -> column.name = "content_length")

    Assert.Equal(SqlInteger, contentLength.columnType)
    Assert.Equal(None, contentLength.unitOfMeasure)
