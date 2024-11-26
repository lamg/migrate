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

let schema1 =
  $"
  {student};
  {view0}
"

let cases = [ schema0, schema1, Ok [ view0 ] ]

let testViewMigration (case: int) (left: string, right: string, r: Result<string list, string>) =
  result {
    let! left = SqlParser.parse ("left", left)
    let! right = SqlParser.parse ("right", right)
    let sortedLeft, _ = Solve.sortFile left
    let sortedRight, _ = Solve.sortFile right
    let statements = Solve.viewMigrationsSql sortedLeft sortedRight
    return statements
  }
  |> runTest case r

[<Fact>]
let ``view migrations`` () =
  cases |> List.mapi testViewMigration |> List.forall id |> Assert.True
