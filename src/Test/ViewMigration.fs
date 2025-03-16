module Test.ViewMigration

open Xunit
open migrate.DeclarativeMigrations
open FsToolkit.ErrorHandling

open CompareObjects

let tableC n =
  $"CREATE TABLE table{n}(id integer NOT NULL)"

let tableD n = $"DROP TABLE table{n}"

let student = "CREATE TABLE student(id integer NOT NULL, name text NOT NULL)"

let likeCoco = "coco%"

let view0 =
  $"CREATE VIEW cocos AS SELECT id, name FROM student WHERE name LIKE '{likeCoco}'"

let schema0 = student

let schema0' =
  $"
  {student};
  {view0}
"

let view1 =
  "CREATE VIEW view0 AS WITH bla AS( SELECT * FROM coco) SELECT * FROM bla b, student s"

let schema1 =
  $"
  {student};
  {view1}
"

let view2 = "CREATE VIEW view0 AS SELECT * FROM coco UNION SELECT * FROM pepe"

let schema2 =
  $"
  {student};
  {view2}
"

let cases =
  [ schema0, schema0', Ok [ view0 ]
    schema0, schema1, Ok [ view1 ]
    schema0, schema2, Ok [ view2 ] ]


let testViewMigration (case: int) (left: string, right: string, r: Result<string list, string>) =
  result {
    let! leftFile = SqlParser.parse ("left", left)
    let! rightFile = SqlParser.parse ("right", right)
    let sortedLeft, _ = Solve.sortFile leftFile
    let sortedRight, _ = Solve.sortFile rightFile
    let statements = Solve.viewMigrationsSql sortedLeft sortedRight
    return statements
  }
  |> runTest case r

[<Fact>]
let ``view migrations`` () =
  cases |> List.mapi testViewMigration |> List.forall id |> Assert.True
