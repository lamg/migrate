module Test.Commands.Init.ExecutionTests

open System
open System.IO
open Microsoft.Data.Sqlite

open MigLib.Commands.Init.Execution
open MigLib.Commands.Schema.Types
open MigLib.Commands.Types
open Xunit

let private createTempDir name =
  let path = Path.Combine(Path.GetTempPath(), $"{name}_{Guid.NewGuid()}")

  Directory.CreateDirectory path |> ignore
  path

let private studentSchema inserts =
  { measureTypes = []
    inserts = inserts
    views = []
    tables =
      [ { name = "student"
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
                        isAutoincrement = false } ]
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
          upsertAnnotations = [] } ]
    indexes = []
    triggers = [] }

let private assertRegularErrorContains expectedText result =
  match result with
  | Error(MigError.Regular message) -> Assert.Contains(expectedText, message)
  | Error error -> failwith $"Expected MigError.Regular, got: {error}"
  | Ok value -> failwith $"Expected error, got: {value}"

[<Fact>]
let ``runInitWithSchema creates and seeds database`` () =
  let tempDir = createTempDir "mig_commands_init_schema"

  try
    let dbPath = Path.Combine(tempDir, "app-main-0123456789abcdef.sqlite")

    let schema =
      studentSchema
        [ { table = "student"
            columns = [ "id"; "name" ]
            values = [ [ Integer 1; String "Ada" ]; [ Integer 2; String "Grace" ] ] } ]

    match runInitWithSchema schema dbPath |> fun task -> task.Result with
    | Ok result ->
      Assert.Equal(dbPath, result.newDbPath)
      Assert.Equal(2L, result.seededRows)

      use connection = new SqliteConnection($"Data Source={dbPath}")
      connection.Open()

      use countCmd = new SqliteCommand("SELECT COUNT(*) FROM student", connection)
      Assert.Equal(2L, countCmd.ExecuteScalar() :?> int64)
    | Error error -> failwith $"Expected init to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``runInitWithSchema fails when database already exists`` () =
  let tempDir = createTempDir "mig_commands_init_existing"

  try
    let dbPath = Path.Combine(tempDir, "existing.sqlite")
    File.WriteAllText(dbPath, "")
    let schema = studentSchema []

    runInitWithSchema schema dbPath
    |> fun task -> task.Result
    |> assertRegularErrorContains "Database already exists"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``runInitWithSchema validates seed column counts`` () =
  let tempDir = createTempDir "mig_commands_init_bad_seed"

  try
    let dbPath = Path.Combine(tempDir, "bad-seed.sqlite")

    let schema =
      studentSchema
        [ { table = "student"
            columns = [ "id"; "name" ]
            values = [ [ Integer 1 ] ] } ]

    runInitWithSchema schema dbPath
    |> fun task -> task.Result
    |> assertRegularErrorContains "has 1 value(s) but 2 column(s)"
  finally
    Directory.Delete(tempDir, true)
