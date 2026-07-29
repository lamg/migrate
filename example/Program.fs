module Example.Program

open System
open System.IO
open System.Reflection
open System.Threading.Tasks
open MigLib
open Example.Db

let private await (t: Task<'a>) = t.GetAwaiter().GetResult()

[<EntryPoint>]
let main _ =
  let dbPath = Path.Combine(Path.GetTempPath(), $"example-{Guid.NewGuid():N}.sqlite")

  match await (migrateEmbedded dbPath (Assembly.GetExecutingAssembly())) with
  | Error e ->
    eprintfn "migrate failed: %s" e
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
      eprintfn "txn failed: %A" e
      1
    | Ok(id, Some row, Some _, count) ->
      printfn "ok id=%d name=%s age=%d count=%d" id row.Name row.Age count
      0
    | Ok _ ->
      eprintfn "unexpected query result"
      1
