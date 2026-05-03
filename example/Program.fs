module Program

open System
open System.IO
open MigLib.Db.Transactions

open ExampleApp.Db

let printStudents (label: string) (students: Student list) =
  printfn "%s" label

  students
  |> List.iter (fun student -> printfn "  id=%d name=%s age=%d" student.Id student.Name student.Age)

[<EntryPoint>]
let main _ =
  let dbPath = Path.Combine(__SOURCE_DIRECTORY__, DbFile)
  let db = dbTxn dbPath

  let result =
    db {
      let! existingStudents = Student.SelectAll
      printStudents "Existing rows in the current database:" existingStudents

      let! insertedId = Student.Insert { Id = 0L; Name = "Carol"; Age = 25L }
      printfn "Inserted Carol with generated id %d" insertedId

      do!
        Student.Upsert
          { Id = insertedId
            Name = "Carol"
            Age = 26L }

      let! carol = Student.SelectByName "Carol"
      let! fuzzyMatch = Student.SelectNameLike "ar"
      let! ensuredStudent = Student.SelectByNameOrInsert { Id = 0L; Name = "Dora"; Age = 19L }
      let! allStudents = Student.SelectAll

      printStudents "Rows returned by generated Student.SelectByName \"Carol\":" carol
      printStudents "Rows returned by generated Student.SelectNameLike \"ar\":" fuzzyMatch

      printfn
        "SelectByNameOrInsert returned: id=%d name=%s age=%d"
        ensuredStudent.Id
        ensuredStudent.Name
        ensuredStudent.Age

      printStudents "All students after generated CRUD operations:" allStudents
      return ()
    }
    |> fun task -> task.Result

  match result with
  | Ok() -> 0
  | Error ex ->
    eprintfn "Example failed: %s" ex.Message
    1
