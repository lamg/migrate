module Example.Program

open System
open System.IO
open System.Threading.Tasks
open MigLib
open Example.Db

let private await (t: Task<'a>) = t.GetAwaiter().GetResult()

let private resolveMigrationsDir () =
  let candidates =
    [ Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Migrations"))
      Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Migrations"))
      Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Migrations")) ]

  candidates
  |> List.tryFind Directory.Exists
  |> Option.defaultValue candidates.Head

[<EntryPoint>]
let main _ =
  let migrations = resolveMigrationsDir ()
  let dbPath =
    Path.Combine(Path.GetTempPath(), "example-" + Guid.NewGuid().ToString("N") + ".sqlite")

  match await (migrateScripts dbPath migrations) with
  | Error e ->
    Console.Error.WriteLine("migrate failed: " + e)
    1
  | Ok() ->
    match
      await (
        dbTxn dbPath {
          let! id =
            Student.insert
              { Name = "Ada"
                Age = 36L }

          let! byId = Student.selectById id
          let! byName = Student.selectOneByName "Ada"
          let! all = Student.selectAll
          return id, byId, byName, all.Length
        }
      )
    with
    | Error e ->
      Console.Error.WriteLine("txn failed: " + string e)
      1
    | Ok(id, Some row, Some _, count) ->
      Console.WriteLine(
        "ok id="
        + string id
        + " name="
        + row.Name
        + " age="
        + string row.Age
        + " count="
        + string count
      )

      0
    | Ok _ ->
      Console.Error.WriteLine "unexpected query result"
      1
