module Program

open System
open System.IO
open MigLib
open MigLib.Db.Transactions

open ExampleApp

let printStudents (label: string) (students: Db.Student list) =
  printfn $"{label}"

  students
  |> List.iter (fun student -> printfn $"  id={student.Id} name={student.Name} age={student.Age}")

[<EntryPoint>]
let main _ =
  let dbRes =
    MigLib.resolveProjectFromGeneratedSchema __SOURCE_DIRECTORY__ None Db.GeneratedSchema

  let db =
    match dbRes.Result with
    | Ok project -> dbTxn project.targetDbPath
    | Error error -> failwithf $"Could not resolve database project: {error}"

  let result =
    db {
      let! existingStudents = Db.Student.SelectAll
      printStudents "Existing rows in the current database:" existingStudents

      let! insertedId = Db.Student.Insert { Id = 0L; Name = "Carol"; Age = 25L }
      printfn $"Inserted Carol with generated id {insertedId}"

      do!
        Db.Student.Upsert
          { Id = insertedId
            Name = "Carol"
            Age = 26L }

      let! carol = Db.Student.SelectByName "Carol"
      let! fuzzyMatch = Db.Student.SelectNameLike "ar"
      let! ensuredStudent = Db.Student.SelectByNameOrInsert { Id = 0L; Name = "Dora"; Age = 19L }
      let! allStudents = Db.Student.SelectAll

      printStudents "Rows returned by generated Student.SelectByName \"Carol\":" carol
      printStudents "Rows returned by generated Student.SelectNameLike \"ar\":" fuzzyMatch

      printfn
        "SelectByNameOrInsert returned: id={ensuredStudent.Id} name={ensuredStudent.Name} age={ensuredStudent.Age}"

      printStudents "All students after generated CRUD operations:" allStudents
      return ()
    }

  match result.Result with
  | Ok() -> 0
  | Error ex ->
    eprintfn $"Example failed: {ex.Message}"
    1
