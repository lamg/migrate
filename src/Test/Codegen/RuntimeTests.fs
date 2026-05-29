module Test.Codegen.RuntimeTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Types
open Xunit

let private createTempDir name =
  let path = Path.Combine(Path.GetTempPath(), $"{name}_{Guid.NewGuid()}")
  Directory.CreateDirectory path |> ignore
  path

let private openConnection dbPath =
  SQLitePCL.Batteries_V2.Init()
  let connection = new SqliteConnection($"Data Source={dbPath}")
  connection.Open()
  connection

let private createStudentTable dbPath =
  use connection = openConnection dbPath

  use cmd =
    new SqliteCommand(
      "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE, age INTEGER NOT NULL DEFAULT 18);",
      connection
    )

  cmd.ExecuteNonQuery() |> ignore

let private unwrapResult result =
  match result with
  | Ok value -> value
  | Error error -> failwith $"Expected operation to succeed, got: {error}"

let private runRuntime (db: DbTxnBuilder) body =
  db.DbRuntime.RunInTransaction MigError.Sqlite body
  |> fun task -> task.Result
  |> unwrapResult

let private connectionTimeout (tx: SqliteTransaction) =
  Task.FromResult(Ok tx.Connection.DefaultTimeout)

let private journalMode (tx: SqliteTransaction) =
  task {
    use cmd = new SqliteCommand("PRAGMA journal_mode;", tx.Connection, tx)
    let! value = cmd.ExecuteScalarAsync()
    return Ok(string value)
  }

let private waitForSignal (signal: TaskCompletionSource<unit>) =
  Assert.True(signal.Task.Wait(TimeSpan.FromSeconds 5.0), "timed out waiting for transaction signal")

let private pendingSignal (signal: TaskCompletionSource<unit>) =
  Assert.False(signal.Task.Wait(TimeSpan.FromMilliseconds 100.0), "transaction entered before it was expected")

let private blockingTransaction (entered: TaskCompletionSource<unit>) (release: TaskCompletionSource<unit>) _ =
  task {
    entered.SetResult()
    do! release.Task
    return Ok()
  }

let private signalingTransaction (entered: TaskCompletionSource<unit>) _ =
  task {
    entered.SetResult()
    return Ok()
  }

[<Fact>]
let ``generated CRUD helper style works against sqlite`` () =
  let tempDir = createTempDir "mig_codegen_runtime"
  let dbPath = Path.Combine(tempDir, "runtime.sqlite")

  try
    createStudentTable dbPath

    let result =
      dbTxn dbPath {
        do! TestCodegenRuntime.Db.Student.DeleteAll

        let! insertedId =
          TestCodegenRuntime.Db.Student.Insert { Id = 0L; Name = "Alice"; Age = 21L }

        let! insertedAgain =
          TestCodegenRuntime.Db.Student.InsertOrIgnore { Id = 0L; Name = "Alice"; Age = 99L }

        let! byId = TestCodegenRuntime.Db.Student.SelectById insertedId
        let! byName = TestCodegenRuntime.Db.Student.SelectByName "Alice"
        let! byLike = TestCodegenRuntime.Db.Student.SelectNameLike "lic"
        let! adults = TestCodegenRuntime.Db.Student.SelectAdults(21L, "%A%")
        let! first = TestCodegenRuntime.Db.Student.SelectOne

        let! reused =
          TestCodegenRuntime.Db.Student.SelectByNameOrInsert { Id = 0L; Name = "Alice"; Age = 21L }

        let! created =
          TestCodegenRuntime.Db.Student.SelectByNameOrInsert { Id = 0L; Name = "Bob"; Age = 25L }

        do!
          TestCodegenRuntime.Db.Student.Upsert
            {
              Id = insertedId
              Name = "Alice"
              Age = 22L
            }

        let! afterUpsert = TestCodegenRuntime.Db.Student.SelectById insertedId
        do! TestCodegenRuntime.Db.Student.Delete created.Id
        let! remaining = TestCodegenRuntime.Db.Student.SelectAll
        do! TestCodegenRuntime.Db.Student.DeleteAdults(21L, "%A%")
        let! afterDeleteWhere = TestCodegenRuntime.Db.Student.SelectAll

        return
          insertedId,
          insertedAgain,
          byId,
          byName,
          byLike,
          adults,
          first,
          reused,
          created,
          afterUpsert,
          remaining,
          afterDeleteWhere
      }
      |> fun task -> task.Result

    match result with
    | Error ex -> failwith $"Expected generated CRUD flow to succeed, got: {ex}"
    | Ok(insertedId,
         insertedAgain,
         byId,
         byName,
         byLike,
         adults,
         first,
         reused,
         created,
         afterUpsert,
         remaining,
         afterDeleteWhere) ->
      Assert.Equal(None, insertedAgain)
      Assert.Equal(Some insertedId, byId |> Option.map _.Id)
      Assert.Equal<int>(1, byName.Length)
      Assert.Equal<int>(1, byLike.Length)
      Assert.Equal<string list>([ "Alice" ], adults |> List.map _.Name)
      Assert.Equal(Some insertedId, first |> Option.map _.Id)
      Assert.Equal(insertedId, reused.Id)
      Assert.Equal("Bob", created.Name)
      Assert.Equal(Some 22L, afterUpsert |> Option.map _.Age)
      Assert.Single remaining |> ignore
      Assert.Equal("Alice", remaining.Head.Name)
      Assert.Empty afterDeleteWhere
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``db connection config applies timeout through runtime and leaves original builder unchanged`` () =
  let tempDir = createTempDir "mig_codegen_runtime_config"
  let dbPath = Path.Combine(tempDir, "runtime.sqlite")

  try
    let original = dbTxn dbPath

    let configured = original |> withBusyTimeout (TimeSpan.FromMilliseconds 1500.0)

    let originalTimeout = runRuntime original connectionTimeout
    let configuredTimeout = runRuntime configured connectionTimeout

    Assert.Equal(2, configuredTimeout)
    Assert.NotEqual(2, originalTimeout)
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``db connection config applies and preserves journal mode through runtime`` () =
  let tempDir = createTempDir "mig_codegen_runtime_journal"
  let dbPath = Path.Combine(tempDir, "runtime.sqlite")

  try
    let walMode =
      dbTxn dbPath
      |> withJournalMode SqliteJournalMode.Wal
      |> fun db -> runRuntime db journalMode

    let preservedMode =
      dbTxn dbPath
      |> withJournalMode SqliteJournalMode.Preserve
      |> fun db -> runRuntime db journalMode

    let deleteMode =
      dbTxn dbPath
      |> withJournalMode SqliteJournalMode.Delete
      |> fun db -> runRuntime db journalMode

    Assert.Equal("wal", walMode)
    Assert.Equal("wal", preservedMode)
    Assert.Equal("delete", deleteMode)
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``read write transactions for the same database path are serialized`` () =
  let tempDir = createTempDir "mig_codegen_runtime_same_path_serial"
  let dbPath = Path.Combine(tempDir, "runtime.sqlite")

  try
    let db = dbTxn dbPath

    let firstEntered =
      TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let releaseFirst =
      TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let secondEntered =
      TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let first =
      db.DbRuntime.RunInTransaction MigError.Sqlite (blockingTransaction firstEntered releaseFirst)

    waitForSignal firstEntered

    let second =
      db.DbRuntime.RunInTransaction MigError.Sqlite (signalingTransaction secondEntered)

    pendingSignal secondEntered
    releaseFirst.SetResult()

    Assert.True(Task.WaitAll([| first :> Task; second :> Task |], TimeSpan.FromSeconds 5.0))

    waitForSignal secondEntered
    unwrapResult first.Result
    unwrapResult second.Result
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``read write transactions for different database paths are independent`` () =
  let tempDir = createTempDir "mig_codegen_runtime_different_path"
  let firstDbPath = Path.Combine(tempDir, "first.sqlite")
  let secondDbPath = Path.Combine(tempDir, "second.sqlite")

  try
    let firstDb = dbTxn firstDbPath
    let secondDb = dbTxn secondDbPath

    let firstEntered =
      TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let releaseFirst =
      TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let secondEntered =
      TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let first =
      firstDb.DbRuntime.RunInTransaction MigError.Sqlite (blockingTransaction firstEntered releaseFirst)

    waitForSignal firstEntered

    let second =
      secondDb.DbRuntime.RunInTransaction MigError.Sqlite (signalingTransaction secondEntered)

    waitForSignal secondEntered
    releaseFirst.SetResult()

    Assert.True(Task.WaitAll([| first :> Task; second :> Task |], TimeSpan.FromSeconds 5.0))

    unwrapResult first.Result
    unwrapResult second.Result
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``read only transactions do not wait for the write transaction gate`` () =
  let tempDir = createTempDir "mig_codegen_runtime_read_only_not_gated"
  let dbPath = Path.Combine(tempDir, "runtime.sqlite")

  try
    createStudentTable dbPath

    let writer = dbTxn dbPath
    let reader = readOnlyDbTxn dbPath

    let writerEntered =
      TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let releaseWriter =
      TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let readerEntered =
      TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let write =
      writer.DbRuntime.RunInTransaction MigError.Sqlite (blockingTransaction writerEntered releaseWriter)

    waitForSignal writerEntered

    let read =
      reader.DbRuntime.RunInTransaction MigError.Sqlite (signalingTransaction readerEntered)

    waitForSignal readerEntered
    releaseWriter.SetResult()

    Assert.True(Task.WaitAll([| write :> Task; read :> Task |], TimeSpan.FromSeconds 5.0))

    unwrapResult write.Result
    unwrapResult read.Result
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``read only transactions can read but reject writes`` () =
  let tempDir = createTempDir "mig_codegen_runtime_read_only"
  let dbPath = Path.Combine(tempDir, "runtime.sqlite")

  try
    createStudentTable dbPath

    dbTxn dbPath {
      let! _ = TestCodegenRuntime.Db.Student.Insert { Id = 0L; Name = "Alice"; Age = 21L }

      return ()
    }
    |> fun task -> task.Result
    |> unwrapResult

    let readResult =
      readOnlyDbTxn dbPath { return! TestCodegenRuntime.Db.Student.SelectAll }
      |> fun task -> task.Result
      |> unwrapResult

    let writeResult =
      readOnlyDbTxn dbPath {
        let! _ = TestCodegenRuntime.Db.Student.Insert { Id = 0L; Name = "Bob"; Age = 25L }

        return ()
      }
      |> fun task -> task.Result

    Assert.Equal<string list>([ "Alice" ], readResult |> List.map _.Name)

    match writeResult with
    | Error _ -> ()
    | Ok() -> failwith "expected read-only transaction write to fail"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``nested read write transaction for same database path reuses active transaction`` () =
  let tempDir = createTempDir "mig_codegen_runtime_nested_write"
  let dbPath = Path.Combine(tempDir, "runtime.sqlite")

  try
    createStudentTable dbPath
    let db = dbTxn dbPath

    let work =
      db {
        let! _ = TestCodegenRuntime.Db.Student.Insert { Id = 0L; Name = "Alice"; Age = 21L }

        let! bobId: int64 =
          db { return! TestCodegenRuntime.Db.Student.Insert { Id = 0L; Name = "Bob"; Age = 25L } }

        return bobId
      }

    Assert.True(work.Wait(TimeSpan.FromSeconds 5.0), "nested write transaction did not complete")
    let bobId = unwrapResult work.Result

    let rows =
      db { return! TestCodegenRuntime.Db.Student.SelectAll }
      |> fun task -> task.Result
      |> unwrapResult

    Assert.True(bobId > 0L)
    Assert.Equal<string list>([ "Alice"; "Bob" ], rows |> List.map _.Name)
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``nested read only transaction for same database path reuses active read write transaction`` () =
  let tempDir = createTempDir "mig_codegen_runtime_nested_read_only"
  let dbPath = Path.Combine(tempDir, "runtime.sqlite")

  try
    createStudentTable dbPath
    let db = dbTxn dbPath
    let readOnlyDb = readOnlyDbTxn dbPath

    let rows =
      db {
        let! _ = TestCodegenRuntime.Db.Student.Insert { Id = 0L; Name = "Alice"; Age = 21L }

        let! rows: TestCodegenRuntime.Db.Student list =
          readOnlyDb { return! TestCodegenRuntime.Db.Student.SelectAll }

        return rows
      }
      |> fun task -> task.Result
      |> unwrapResult

    Assert.Equal<string list>([ "Alice" ], rows |> List.map _.Name)
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``nested read write transaction inside read only transaction fails`` () =
  let tempDir = createTempDir "mig_codegen_runtime_nested_read_only_write"
  let dbPath = Path.Combine(tempDir, "runtime.sqlite")

  try
    createStudentTable dbPath
    let db = dbTxn dbPath
    let readOnlyDb = readOnlyDbTxn dbPath

    let result =
      readOnlyDb {
        let! insertedId: int64 =
          db { return! TestCodegenRuntime.Db.Student.Insert { Id = 0L; Name = "Alice"; Age = 21L } }

        return insertedId
      }
      |> fun task -> task.Result

    match result with
    | Error _ -> ()
    | Ok _ -> failwith "expected nested read-write transaction inside read-only transaction to fail"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``db connection config rejects negative busy timeout`` () =
  Assert.Throws<ArgumentOutOfRangeException>(fun () ->
    dbTxn "unused.sqlite" |> withBusyTimeout (TimeSpan.FromSeconds -1.0) |> ignore)
  |> ignore
