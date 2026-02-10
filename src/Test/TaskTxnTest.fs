module Test.TaskTxnTest

open System.IO
open System.Threading.Tasks
open Xunit
open Microsoft.Data.Sqlite

open migrate.Db

/// Helper to create a temp database file path
let createTempDb () =
  let tempPath =
    Path.Combine(Path.GetTempPath(), $"test_db_{System.Guid.NewGuid()}.db")

  tempPath

/// Helper to initialize a database with a test table
let initializeDb (dbPath: string) =
  let connString = $"Data Source={dbPath}"
  use conn = new SqliteConnection(connString)
  conn.Open()
  use cmd = conn.CreateCommand()

  cmd.CommandText <- "CREATE TABLE test_table (id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT NOT NULL)"

  cmd.ExecuteNonQuery() |> ignore

/// Helper to count rows in test_table
let countRows (dbPath: string) : int =
  let connString = $"Data Source={dbPath}"
  use conn = new SqliteConnection(connString)
  conn.Open()
  use cmd = conn.CreateCommand()
  cmd.CommandText <- "SELECT COUNT(*) FROM test_table"
  cmd.ExecuteScalar() :?> int64 |> int

[<Fact>]
let ``taskTxn commits successful transaction`` () =
  let dbPath = createTempDb ()

  try
    initializeDb dbPath

    let insertTask =
      taskTxn dbPath {
        let! _ =
          fun (tx: SqliteTransaction) ->
            task {
              use cmd = tx.Connection.CreateCommand()
              cmd.Transaction <- tx
              cmd.CommandText <- "INSERT INTO test_table (value) VALUES ('test1')"
              let! rows = cmd.ExecuteNonQueryAsync()
              return Ok rows
            }

        return!
          fun (tx: SqliteTransaction) ->
            task {
              use cmd = tx.Connection.CreateCommand()
              cmd.Transaction <- tx
              cmd.CommandText <- "INSERT INTO test_table (value) VALUES ('test2')"
              let! rows = cmd.ExecuteNonQueryAsync()
              return Ok rows
            }
      }

    let result = insertTask.Result

    match result with
    | Ok _ ->
      // Verify both rows were committed
      let rowCount = countRows dbPath
      Assert.Equal(2, rowCount)
    | Error ex -> Assert.Fail $"Transaction should succeed: {ex.Message}"

  finally
    if File.Exists dbPath then
      File.Delete dbPath

[<Fact>]
let ``taskTxn rolls back on error`` () =
  let dbPath = createTempDb ()

  try
    initializeDb dbPath

    let insertTask =
      taskTxn dbPath {
        let! _ =
          fun (tx: SqliteTransaction) ->
            task {
              use cmd = tx.Connection.CreateCommand()
              cmd.Transaction <- tx
              cmd.CommandText <- "INSERT INTO test_table (value) VALUES ('test1')"
              let! rows = cmd.ExecuteNonQueryAsync()
              return Ok rows
            }

        // This should fail due to constraint violation (inserting duplicate primary key)
        return!
          fun (tx: SqliteTransaction) ->
            task {
              use cmd = tx.Connection.CreateCommand()
              cmd.Transaction <- tx
              // Force an error by inserting invalid SQL
              cmd.CommandText <- "INSERT INTO nonexistent_table (value) VALUES ('test2')"

              try
                let! _ = cmd.ExecuteNonQueryAsync()
                return Ok 1
              with :? SqliteException as ex ->
                return Error ex
            }
      }

    let result = insertTask.Result

    match result with
    | Ok _ -> Assert.Fail "Transaction should have failed"
    | Error _ ->
      // Verify transaction was rolled back - no rows should exist
      let rowCount = countRows dbPath
      Assert.Equal(0, rowCount)

  finally
    if File.Exists dbPath then
      File.Delete dbPath

[<Fact>]
let ``taskTxn returns value from computation`` () =
  let dbPath = createTempDb ()

  try
    initializeDb dbPath

    let insertAndReturnIdTask =
      taskTxn dbPath {
        let! insertedId =
          fun (tx: SqliteTransaction) ->
            task {
              use cmd = tx.Connection.CreateCommand()
              cmd.Transaction <- tx
              cmd.CommandText <- "INSERT INTO test_table (value) VALUES ('test'); SELECT last_insert_rowid()"
              let! id = cmd.ExecuteScalarAsync()
              return Ok(id :?> int64)
            }

        return insertedId
      }

    let result = insertAndReturnIdTask.Result

    match result with
    | Ok id ->
      Assert.True(id > 0L, "Should return valid ID")
      // Verify row was committed
      let rowCount = countRows dbPath
      Assert.Equal(1, rowCount)
    | Error ex -> Assert.Fail $"Transaction should succeed: {ex.Message}"

  finally
    if File.Exists dbPath then
      File.Delete dbPath

[<Fact>]
let ``taskTxn handles async delays`` () =
  let dbPath = createTempDb ()

  try
    initializeDb dbPath

    let insertWithDelayTask =
      taskTxn dbPath {
        let! _ =
          fun (tx: SqliteTransaction) ->
            task {
              // Simulate async work
              do! Task.Delay 10

              use cmd = tx.Connection.CreateCommand()
              cmd.Transaction <- tx
              cmd.CommandText <- "INSERT INTO test_table (value) VALUES ('delayed')"
              let! rows = cmd.ExecuteNonQueryAsync()
              return Ok rows
            }

        return "success"
      }

    let result = insertWithDelayTask.Result

    match result with
    | Ok msg ->
      Assert.Equal("success", msg)
      let rowCount = countRows dbPath
      Assert.Equal(1, rowCount)
    | Error ex -> Assert.Fail $"Transaction should succeed: {ex.Message}"

  finally
    if File.Exists dbPath then
      File.Delete dbPath

[<Fact>]
let ``taskTxn handles database connection errors`` () =
  let dbPath = "/nonexistent/path/to/database.db"

  let insertTask =
    taskTxn dbPath { return! fun (tx: SqliteTransaction) -> task { return Ok "should not reach here" } }

  let result = insertTask.Result

  match result with
  | Ok _ -> Assert.Fail "Should fail with connection error"
  | Error ex -> Assert.Contains("unable to open database", ex.Message.ToLower())

[<Fact>]
let ``taskTxn for loop inserts multiple rows in one transaction`` () =
  let dbPath = createTempDb ()

  try
    initializeDb dbPath

    let insertTask =
      taskTxn dbPath {
        for item in [ "a"; "b"; "c" ] do
          let! _ =
            fun (tx: SqliteTransaction) ->
              task {
                use cmd = tx.Connection.CreateCommand()
                cmd.Transaction <- tx
                cmd.CommandText <- $"INSERT INTO test_table (value) VALUES ('{item}')"
                let! rows = cmd.ExecuteNonQueryAsync()
                return Ok rows
              }

          ()
      }

    let result = insertTask.Result

    match result with
    | Ok() ->
      let rowCount = countRows dbPath
      Assert.Equal(3, rowCount)
    | Error ex -> Assert.Fail $"Transaction should succeed: {ex.Message}"

  finally
    if File.Exists dbPath then
      File.Delete dbPath

[<Fact>]
let ``taskTxn for loop rolls back all on error`` () =
  let dbPath = createTempDb ()

  try
    initializeDb dbPath

    let insertTask =
      taskTxn dbPath {
        for item in [ "ok1"; "ok2"; "FAIL" ] do
          let! _ =
            fun (tx: SqliteTransaction) ->
              task {
                if item = "FAIL" then
                  use cmd = tx.Connection.CreateCommand()
                  cmd.Transaction <- tx
                  cmd.CommandText <- "INSERT INTO nonexistent_table (value) VALUES ('x')"

                  try
                    let! _ = cmd.ExecuteNonQueryAsync()
                    return Ok 1
                  with :? SqliteException as ex ->
                    return Error ex
                else
                  use cmd = tx.Connection.CreateCommand()
                  cmd.Transaction <- tx
                  cmd.CommandText <- $"INSERT INTO test_table (value) VALUES ('{item}')"
                  let! rows = cmd.ExecuteNonQueryAsync()
                  return Ok rows
              }

          ()
      }

    let result = insertTask.Result

    match result with
    | Ok() -> Assert.Fail "Transaction should have failed"
    | Error _ ->
      let rowCount = countRows dbPath
      Assert.Equal(0, rowCount)

  finally
    if File.Exists dbPath then
      File.Delete dbPath

[<Fact>]
let ``taskTxn for loop with empty sequence succeeds`` () =
  let dbPath = createTempDb ()

  try
    initializeDb dbPath

    let insertTask =
      taskTxn dbPath {
        for _ in ([]: string list) do
          let! _ = fun (tx: SqliteTransaction) -> task { return Ok 1 }

          ()
      }

    let result = insertTask.Result

    match result with
    | Ok() -> Assert.Equal(0, countRows dbPath)
    | Error ex -> Assert.Fail $"Empty for loop should succeed: {ex.Message}"

  finally
    if File.Exists dbPath then
      File.Delete dbPath

[<Fact>]
let ``taskTxn for loop followed by bind`` () =
  let dbPath = createTempDb ()

  try
    initializeDb dbPath

    let insertTask =
      taskTxn dbPath {
        for item in [ "a"; "b" ] do
          let! _ =
            fun (tx: SqliteTransaction) ->
              task {
                use cmd = tx.Connection.CreateCommand()
                cmd.Transaction <- tx
                cmd.CommandText <- $"INSERT INTO test_table (value) VALUES ('{item}')"
                let! rows = cmd.ExecuteNonQueryAsync()
                return Ok rows
              }

          ()

        let! count =
          fun (tx: SqliteTransaction) ->
            task {
              use cmd = tx.Connection.CreateCommand()
              cmd.Transaction <- tx
              cmd.CommandText <- "SELECT COUNT(*) FROM test_table"
              let! result = cmd.ExecuteScalarAsync()
              return Ok(result :?> int64)
            }

        return count
      }

    let result = insertTask.Result

    match result with
    | Ok count -> Assert.Equal(2L, count)
    | Error ex -> Assert.Fail $"Transaction should succeed: {ex.Message}"

  finally
    if File.Exists dbPath then
      File.Delete dbPath
