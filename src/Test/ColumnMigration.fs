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
    let! leftFile = FParsecSqlParser.parseSqlFile ("left", left)
    let! rightFile = FParsecSqlParser.parseSqlFile ("right", right)
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

[<Fact>]
let ``column addition when other tables have FK references`` () =
  let left = $"{teacher};{enrollment}"
  let right = $"{teacherWithAge};{enrollment}"

  let leftFile = FParsecSqlParser.parseSqlFile ("left", left) |> Result.defaultWith failwith
  let rightFile = FParsecSqlParser.parseSqlFile ("right", right) |> Result.defaultWith failwith

  let actual = Migration.migration (leftFile, rightFile) |> Result.defaultWith (fun e -> failwith $"%A{e}")

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
