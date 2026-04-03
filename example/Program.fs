module Program

open System
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open Mig.HotMigration
open MigLib.Db
open MigLib.Util

open Db

let printStudents (label: string) (students: Student list) =
  printfn "%s" label

  students
  |> List.iter (fun student -> printfn "  id=%d name=%s age=%d" student.Id student.Name student.Age)

[<EntryPoint>]
let main _ =
  let result =
    taskResult {
      let! (db: DbTxnBuilder) = startService __SOURCE_DIRECTORY__ DbFile SchemaIdentity Schema CancellationToken.None

      printfn "Opened database at: %s" db.DbPath

      let! (students: Student list) =
        db {
          do! Student.DeleteAll
          let! insertedId = Student.Insert { Id = 0L; Name = "Carol"; Age = 25L }
          let! carol = Student.SelectByName "Carol"
          let! allStudents = Student.SelectAll

          printfn "Inserted Carol with generated id %d" insertedId
          printStudents "Rows returned by generated Student.SelectByName \"Carol\":" carol
          return allStudents
        }

      printStudents "All students:" students
      return ()
    }
    |> Async.AwaitTask
    |> Async.RunSynchronously

  match result with
  | Ok() -> 0
  | Error ex ->
    eprintfn "Example failed: %s" ex.Message
    1
