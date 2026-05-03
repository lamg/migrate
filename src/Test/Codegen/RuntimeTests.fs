module Test.Codegen.RuntimeTests

open System
open System.IO
open Microsoft.Data.Sqlite
open MigLib.Db.Transactions
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

[<Fact>]
let ``generated CRUD helper style works against sqlite`` () =
  let tempDir = createTempDir "mig_codegen_runtime"
  let dbPath = Path.Combine(tempDir, "runtime.sqlite")

  try
    createStudentTable dbPath

    let result =
      dbTxn dbPath {
        do! TestCodegenRuntime.Db.Student.DeleteAll

        let! insertedId = TestCodegenRuntime.Db.Student.Insert { Id = 0L; Name = "Alice"; Age = 21L }
        let! insertedAgain = TestCodegenRuntime.Db.Student.InsertOrIgnore { Id = 0L; Name = "Alice"; Age = 99L }
        let! byId = TestCodegenRuntime.Db.Student.SelectById insertedId
        let! byName = TestCodegenRuntime.Db.Student.SelectByName "Alice"
        let! byLike = TestCodegenRuntime.Db.Student.SelectNameLike "lic"
        let! first = TestCodegenRuntime.Db.Student.SelectOne
        let! reused = TestCodegenRuntime.Db.Student.SelectByNameOrInsert { Id = 0L; Name = "Alice"; Age = 21L }
        let! created = TestCodegenRuntime.Db.Student.SelectByNameOrInsert { Id = 0L; Name = "Bob"; Age = 25L }

        do!
          TestCodegenRuntime.Db.Student.Upsert
            { Id = insertedId
              Name = "Alice"
              Age = 22L }

        let! afterUpsert = TestCodegenRuntime.Db.Student.SelectById insertedId
        do! TestCodegenRuntime.Db.Student.Delete created.Id
        let! remaining = TestCodegenRuntime.Db.Student.SelectAll
        return insertedId, insertedAgain, byId, byName, byLike, first, reused, created, afterUpsert, remaining
      }
      |> fun task -> task.Result

    match result with
    | Error ex -> failwith $"Expected generated CRUD flow to succeed, got: {ex.Message}"
    | Ok(insertedId, insertedAgain, byId, byName, byLike, first, reused, created, afterUpsert, remaining) ->
      Assert.Equal(None, insertedAgain)
      Assert.Equal(Some insertedId, byId |> Option.map _.Id)
      Assert.Equal<int>(1, byName.Length)
      Assert.Equal<int>(1, byLike.Length)
      Assert.Equal(Some insertedId, first |> Option.map _.Id)
      Assert.Equal(insertedId, reused.Id)
      Assert.Equal("Bob", created.Name)
      Assert.Equal(Some 22L, afterUpsert |> Option.map _.Age)
      Assert.Single remaining |> ignore
      Assert.Equal("Alice", remaining.Head.Name)
  finally
    Directory.Delete(tempDir, true)
