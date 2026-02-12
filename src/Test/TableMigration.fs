module Test.TableMigration

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open CompareObjects

let tableC n =
  $"CREATE TABLE table{n}(id integer NOT NULL)"

let tableD n = $"DROP TABLE table{n}"

let student = "CREATE TABLE student(id integer NOT NULL, name text NOT NULL)"

let table1Fk0 =
  "CREATE TABLE table1(id integer NOT NULL, FOREIGN KEY(t0_id) REFERENCES table0(id))"

let table2Fk1 =
  "CREATE TABLE table2(id integer NOT NULL, FOREIGN KEY(t1_id) REFERENCES table1(id))"

let foreignKeys0 =
  $"
  {table2Fk1};
  {table1Fk0};
  {tableC 0};"

let foreignKeys1 =
  $"
  {tableC 0};
  {table1Fk0};
  {table2Fk1};
"

let dropSorted = [ tableD 2; tableD 1; tableD 0 ]
let createSorted = [ tableC 0; table1Fk0; table2Fk1 ]

let cases =
  [ "", tableC 0, Ok [ tableC 0 ]
    tableC 0, tableC 1, Ok [ "ALTER TABLE table0 RENAME TO table1" ]
    tableC 0, student, Ok [ student; tableD 0 ]
    foreignKeys0, "", Ok dropSorted
    foreignKeys1, "", Ok dropSorted
    "", foreignKeys0, Ok createSorted ]

let testTableMigration (case: int) (left: string, right: string, r: Result<string list, string>) =
  result {
    let! left = SqlParserWrapper.parseSqlFile ("left", left)
    let! right = SqlParserWrapper.parseSqlFile ("right", right)
    let sortedLeft, _ = Solve.sortFile left
    let sortedRight, _ = Solve.sortFile right
    let statements = Solve.tableMigrationsSql sortedLeft sortedRight
    return statements
  }
  |> runTest case r

[<Fact>]
let ``My test`` () =
  cases |> List.mapi testTableMigration |> List.forall id |> Assert.True
