module MigrateTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open MigLib

let private await (t: Task<'a>) = t.GetAwaiter().GetResult()

let private withTempRoot (action: string -> string -> unit) =
  let root =
    Path.Combine(Path.GetTempPath(), "mig-mig-" + Guid.NewGuid().ToString("N"))

  let migrations = Path.Combine(root, "migrations")
  let dbPath = Path.Combine(root, "app.sqlite")
  Directory.CreateDirectory migrations |> ignore

  try
    action migrations dbPath
  finally
    try
      Directory.Delete(root, true)
    with _ ->
      ()

[<Fact>]
let ``migrateScripts applies sql files`` () =
  withTempRoot (fun migrations dbPath ->
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
      | Ok count -> Assert.Equal(1L, Convert.ToInt64 count))

[<Fact>]
let ``migrateScripts is idempotent via SchemaVersions`` () =
  withTempRoot (fun migrations dbPath ->
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
    | Ok() -> ()

    match await (migrateScripts dbPath migrations) with
    | Error e -> Assert.Fail e
    | Ok() -> ()

    match await (dbTxn dbPath { return! Query.scalar "SELECT COUNT(*) FROM SchemaVersions" [] }) with
    | Error e -> Assert.Fail(string e)
    | Ok count -> Assert.Equal(1L, Convert.ToInt64 count))

[<Fact>]
let ``migrateScripts does not journal a failed script`` () =
  withTempRoot (fun migrations dbPath ->
    File.WriteAllText(Path.Combine(migrations, "001_bad.sql"), "CREATE TABLE broken (;")

    match await (migrateScripts dbPath migrations) with
    | Ok() -> Assert.Fail "expected migration failure"
    | Error _ ->
      match await (dbTxn dbPath { return! Query.scalar "SELECT COUNT(*) FROM SchemaVersions" [] }) with
      | Error e -> Assert.Fail(string e)
      | Ok count -> Assert.Equal(0L, Convert.ToInt64 count))

[<Fact>]
let ``migrateScripts rejects missing directory`` () =
  let dbPath =
    Path.Combine(Path.GetTempPath(), "mig-missing-" + Guid.NewGuid().ToString("N") + ".sqlite")

  match await (migrateScripts dbPath "/no/such/migrations/dir") with
  | Ok() -> Assert.Fail "expected error"
  | Error msg -> Assert.Contains("not found", msg)
