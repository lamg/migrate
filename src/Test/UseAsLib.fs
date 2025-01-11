module Test.UseAsLib

open Xunit
open migrate.Execution.Exec
open FsToolkit.ErrorHandling

[<Fact>]
let useAsLib() =
  let sources = [
    { name = "schema0.sql"; content="CREATE TABLE table0(id INTEGER NOT NULL)" }
  ] 

  result {
    let! statements = migrationStatementsForDb ("/path/to/db.sqlite", sources)
    let! results = executeMigration statements
    return results
  }
  |> Result.isError // because the path does not exists
  |> Assert.True