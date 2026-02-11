module Test.SeedExecutionTest

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite

open migrate.Execution

let createTempDb () =
  Path.Combine(Path.GetTempPath(), $"seed_test_{Guid.NewGuid()}.db")

let initializeCyclicFkSchema (dbPath: string) =
  use conn = new SqliteConnection($"Data Source={dbPath}")
  conn.Open()

  use pragmaCmd = conn.CreateCommand()
  pragmaCmd.CommandText <- "PRAGMA foreign_keys=ON"
  pragmaCmd.ExecuteNonQuery() |> ignore

  use createA = conn.CreateCommand()

  createA.CommandText <-
    "CREATE TABLE a(id INTEGER PRIMARY KEY, b_id INTEGER NOT NULL, FOREIGN KEY(b_id) REFERENCES b(id))"

  createA.ExecuteNonQuery() |> ignore

  use createB = conn.CreateCommand()

  createB.CommandText <-
    "CREATE TABLE b(id INTEGER PRIMARY KEY, a_id INTEGER NOT NULL, FOREIGN KEY(a_id) REFERENCES a(id))"

  createB.ExecuteNonQuery() |> ignore

let countRows (dbPath: string) (tableName: string) =
  use conn = new SqliteConnection($"Data Source={dbPath}")
  conn.Open()
  use cmd = conn.CreateCommand()
  cmd.CommandText <- $"SELECT COUNT(*) FROM {tableName}"
  cmd.ExecuteScalar() :?> int64 |> int

[<Fact>]
let ``executeSeed disables foreign key checks while applying seed statements`` () =
  let dbPath = createTempDb ()

  let statements =
    [ "INSERT OR REPLACE INTO a(id, b_id) VALUES (1, 1)"
      "INSERT OR REPLACE INTO b(id, a_id) VALUES (1, 1)" ]

  try
    initializeCyclicFkSchema dbPath

    match Exec.executeSeedForDb dbPath statements with
    | Ok _ ->
      Assert.Equal(1, countRows dbPath "a")
      Assert.Equal(1, countRows dbPath "b")
    | Error e -> Assert.Fail $"Seed execution should succeed with foreign key checks disabled: {e}"
  finally
    if File.Exists dbPath then
      File.Delete dbPath
