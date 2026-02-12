module Test.ColumnMigration

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open CompareObjects

let student = "CREATE TABLE student(id integer NOT NULL, name text NOT NULL)"

let studentWithAge =
  "CREATE TABLE student(id integer NOT NULL, name text NOT NULL, age integer NOT NULL)"

let studentWithBio =
  "CREATE TABLE student(id integer NOT NULL, name text NOT NULL, bio text NOT NULL)"

let studentWithScore =
  "CREATE TABLE student(id integer NOT NULL, name text NOT NULL, score real NOT NULL)"

let cases =
  [ // adding integer column uses 0 as default
    student,
    studentWithAge,
    Ok
      [ "PRAGMA foreign_keys=OFF"
        "CREATE TABLE student_temp(id integer NOT NULL, name text NOT NULL, age integer NOT NULL)"
        "INSERT INTO student_temp(id, name, age) SELECT id, name, 0 FROM student"
        "DROP TABLE student"
        "ALTER TABLE student_temp RENAME TO student"
        "PRAGMA foreign_keys=ON" ]
    // adding text column uses '' as default
    student,
    studentWithBio,
    Ok
      [ "PRAGMA foreign_keys=OFF"
        "CREATE TABLE student_temp(id integer NOT NULL, name text NOT NULL, bio text NOT NULL)"
        "INSERT INTO student_temp(id, name, bio) SELECT id, name, '' FROM student"
        "DROP TABLE student"
        "ALTER TABLE student_temp RENAME TO student"
        "PRAGMA foreign_keys=ON" ]
    // adding real column uses 0.0 as default
    student,
    studentWithScore,
    Ok
      [ "PRAGMA foreign_keys=OFF"
        "CREATE TABLE student_temp(id integer NOT NULL, name text NOT NULL, score real NOT NULL)"
        "INSERT INTO student_temp(id, name, score) SELECT id, name, 0.0 FROM student"
        "DROP TABLE student"
        "ALTER TABLE student_temp RENAME TO student"
        "PRAGMA foreign_keys=ON" ] ]

let testColumnMigration (case: int) (left: string, right: string, r: Result<string list, string>) =
  result {
    let! leftFile = SqlParserWrapper.parseSqlFile ("left", left)
    let! rightFile = SqlParserWrapper.parseSqlFile ("right", right)
    let statements = Solve.columnMigrations leftFile.tables rightFile.tables
    return statements
  }
  |> runTest case r

[<Fact>]
let ``column addition with defaults`` () =
  cases |> List.mapi testColumnMigration |> List.forall id |> Assert.True

let teacher = "CREATE TABLE teacher(id integer PRIMARY KEY, name text NOT NULL)"

let teacherWithAge =
  "CREATE TABLE teacher(id integer PRIMARY KEY, name text NOT NULL, age integer NOT NULL)"

let enrollment =
  "CREATE TABLE enrollment(id integer PRIMARY KEY, teacher_id integer NOT NULL, FOREIGN KEY(teacher_id) REFERENCES teacher(id))"

let parent = "CREATE TABLE parent(id integer PRIMARY KEY)"

let childWithInlineFkNoCascade =
  "CREATE TABLE child(id integer PRIMARY KEY, parent_id integer REFERENCES parent(id))"

let childWithInlineFkCascade =
  "CREATE TABLE child(id integer PRIMARY KEY, parent_id integer REFERENCES parent(id) ON DELETE CASCADE)"

let childView = "CREATE VIEW child_view AS SELECT c.id, c.parent_id FROM child c"

[<Fact>]
let ``column addition when other tables have FK references`` () =
  let left = $"{teacher};{enrollment}"
  let right = $"{teacherWithAge};{enrollment}"

  let leftFile =
    SqlParserWrapper.parseSqlFile ("left", left) |> Result.defaultWith failwith

  let rightFile =
    SqlParserWrapper.parseSqlFile ("right", right) |> Result.defaultWith failwith

  let actual =
    Migration.migration (leftFile, rightFile)
    |> Result.defaultWith (fun e -> failwith $"%A{e}")

  printfn "Generated SQL:"
  actual |> List.iter (printfn "  %s")

  let expected =
    [ "PRAGMA foreign_keys=OFF"
      "CREATE TABLE teacher_temp(id integer PRIMARY KEY, name text NOT NULL, age integer NOT NULL)"
      "INSERT INTO teacher_temp(id, name, age) SELECT id, name, 0 FROM teacher"
      "DROP TABLE teacher"
      "ALTER TABLE teacher_temp RENAME TO teacher"
      "PRAGMA foreign_keys=ON" ]

  Assert.Equal<string list>(expected, actual)

[<Fact>]
let ``column FK ON DELETE CASCADE is parsed`` () =
  let sql = $"{parent};{childWithInlineFkCascade}"

  let parsed =
    SqlParserWrapper.parseSqlFile ("schema", sql) |> Result.defaultWith failwith

  let childTable = parsed.tables |> List.find (fun t -> t.name = "child")
  let parentId = childTable.columns |> List.find (fun c -> c.name = "parent_id")

  let fk =
    parentId.constraints
    |> List.choose (function
      | Types.ForeignKey foreignKey -> Some foreignKey
      | _ -> None)
    |> List.tryHead
    |> Option.defaultWith (fun () -> failwith "Expected foreign key on child.parent_id")

  Assert.Equal<Types.FkAction option>(Some Types.Cascade, fk.onDelete)

[<Fact>]
let ``changing column FK to ON DELETE CASCADE triggers migration`` () =
  let left = $"{parent};{childWithInlineFkNoCascade}"
  let right = $"{parent};{childWithInlineFkCascade}"

  let leftFile =
    SqlParserWrapper.parseSqlFile ("left", left) |> Result.defaultWith failwith

  let rightFile =
    SqlParserWrapper.parseSqlFile ("right", right) |> Result.defaultWith failwith

  let actual =
    Migration.migration (leftFile, rightFile)
    |> Result.defaultWith (fun e -> failwith $"%A{e}")

  let expected =
    [ "PRAGMA foreign_keys=OFF"
      "CREATE TABLE child_temp(id integer PRIMARY KEY, parent_id integer REFERENCES parent(id) ON DELETE CASCADE)"
      "INSERT INTO child_temp(id, parent_id) SELECT id, parent_id FROM child"
      "DROP TABLE child"
      "ALTER TABLE child_temp RENAME TO child"
      "PRAGMA foreign_keys=ON" ]

  Assert.Equal<string list>(expected, actual)

[<Fact>]
let ``recreating table drops and recreates dependent views`` () =
  let left = $"{parent};{childWithInlineFkNoCascade};{childView}"
  let right = $"{parent};{childWithInlineFkCascade};{childView}"

  let leftFile =
    SqlParserWrapper.parseSqlFile ("left", left) |> Result.defaultWith failwith

  let rightFile =
    SqlParserWrapper.parseSqlFile ("right", right) |> Result.defaultWith failwith

  let actual =
    Migration.migration (leftFile, rightFile)
    |> Result.defaultWith (fun e -> failwith $"%A{e}")

  let ixDropView = actual |> List.findIndex ((=) "DROP VIEW child_view")

  let ixCreateTemp =
    actual |> List.findIndex (fun s -> s.StartsWith "CREATE TABLE child_temp")

  let ixRename =
    actual |> List.findIndex ((=) "ALTER TABLE child_temp RENAME TO child")

  let ixCreateView =
    actual |> List.findIndex (fun s -> s.StartsWith "CREATE VIEW child_view AS")

  Assert.True(ixDropView < ixCreateTemp)
  Assert.True(ixCreateView > ixRename)
