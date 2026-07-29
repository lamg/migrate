module MigrateTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open MigLib

let private await (t: Task<'a>) = t.GetAwaiter().GetResult()

[<Fact>]
let ``migrateScripts applies sql files`` () =
  let root = Path.Combine(Path.GetTempPath(), $"mig-mig-{Guid.NewGuid():N}")
  let migrations = Path.Combine(root, "migrations")
  let dbPath = Path.Combine(root, "app.sqlite")
  Directory.CreateDirectory migrations |> ignore

  try
    File.WriteAllText(
      Path.Combine(migrations, "001_init.sql"),
      """
CREATE TABLE item (
  id INTEGER NOT NULL PRIMARY KEY,
  name TEXT NOT NULL
) STRICT;
"""
    )

    match await (migrateScripts dbPath migrations) with
    | Error e -> Assert.Fail e
    | Ok() ->
      match
        await (
          dbTxn dbPath {
            return! Query.scalar "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='item'" []
          }
        )
      with
      | Error e -> Assert.Fail(string e)
      | Ok count -> Assert.Equal(1L, Convert.ToInt64 count)
  finally
    try
      Directory.Delete(root, true)
    with _ ->
      ()
