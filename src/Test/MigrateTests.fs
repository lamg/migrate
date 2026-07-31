module MigrateTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open MigLib
open MigLib.Migrate

let private await (t: Task<'a>) = t.GetAwaiter().GetResult()

let private withTempDb (action: string -> unit) =
  let dbPath =
    Path.Combine(Path.GetTempPath(), "mig-mig-" + Guid.NewGuid().ToString("N") + ".sqlite")

  try
    action dbPath
  finally
    try
      if File.Exists dbPath then
        File.Delete dbPath
    with _ ->
      ()

    for suffix in [ "-wal"; "-shm" ] do
      try
        let side = dbPath + suffix

        if File.Exists side then
          File.Delete side
      with _ ->
        ()

[<Fact>]
let ``migrateScripts applies named scripts`` () =
  withTempDb (fun dbPath ->
    let scripts =
      [ "001_init.sql",
        """
CREATE TABLE item (
  id INTEGER NOT NULL PRIMARY KEY,
  name TEXT NOT NULL
) STRICT;
""" ]

    match await (migrateScripts dbPath scripts) with
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
  withTempDb (fun dbPath ->
    let scripts =
      [ "001_init.sql",
        """
CREATE TABLE item (
  id INTEGER NOT NULL PRIMARY KEY,
  name TEXT NOT NULL
) STRICT;
""" ]

    match await (migrateScripts dbPath scripts) with
    | Error e -> Assert.Fail e
    | Ok() -> ()

    match await (migrateScripts dbPath scripts) with
    | Error e -> Assert.Fail e
    | Ok() -> ()

    match await (dbTxn dbPath { return! Query.scalar "SELECT COUNT(*) FROM SchemaVersions" [] }) with
    | Error e -> Assert.Fail(string e)
    | Ok count -> Assert.Equal(1L, Convert.ToInt64 count))

[<Fact>]
let ``migrateScripts does not journal a failed script`` () =
  withTempDb (fun dbPath ->
    let scripts = [ "001_bad.sql", "CREATE TABLE broken (;" ]

    match await (migrateScripts dbPath scripts) with
    | Ok() -> Assert.Fail "expected migration failure"
    | Error _ ->
      match await (dbTxn dbPath { return! Query.scalar "SELECT COUNT(*) FROM SchemaVersions" [] }) with
      | Error e -> Assert.Fail(string e)
      | Ok count -> Assert.Equal(0L, Convert.ToInt64 count))

[<Fact>]
let ``migrateScripts empty list succeeds`` () =
  withTempDb (fun dbPath ->
    match await (migrateScripts dbPath []) with
    | Error e -> Assert.Fail e
    | Ok() ->
      match await (dbTxn dbPath { return! Query.scalar "SELECT COUNT(*) FROM SchemaVersions" [] }) with
      | Error e -> Assert.Fail(string e)
      | Ok count -> Assert.Equal(0L, Convert.ToInt64 count))

[<Fact>]
let ``migrateScripts applies in list order`` () =
  withTempDb (fun dbPath ->
    // Names intentionally reverse of alphabetical order; list order is authoritative.
    let scripts =
      [ "002_second.sql", "CREATE TABLE second (id INTEGER NOT NULL PRIMARY KEY) STRICT;"
        "001_first.sql", "CREATE TABLE first (id INTEGER NOT NULL PRIMARY KEY) STRICT;" ]

    match await (migrateScripts dbPath scripts) with
    | Error e -> Assert.Fail e
    | Ok() ->
      match
        await (
          dbTxn dbPath {
            return!
              Query.queryList "SELECT [ScriptName] FROM [SchemaVersions] ORDER BY [SchemaVersionID]" [] (fun r ->
                r.GetString 0)
          }
        )
      with
      | Error e -> Assert.Fail(string e)
      | Ok names -> Assert.Equal<string list>([ "002_second.sql"; "001_first.sql" ], names))

[<Fact>]
let ``loadScriptsFromDirectory rejects missing directory`` () =
  match loadScriptsFromDirectory "/no/such/migrations/dir" with
  | Ok _ -> Assert.Fail "expected error"
  | Error msg -> Assert.Contains("not found", msg)

[<Fact>]
let ``loadScriptsFromDirectory orders by file name`` () =
  let root =
    Path.Combine(Path.GetTempPath(), "mig-load-" + Guid.NewGuid().ToString("N"))

  let migrations = Path.Combine(root, "migrations")
  Directory.CreateDirectory migrations |> ignore

  try
    File.WriteAllText(Path.Combine(migrations, "002_b.sql"), "B")
    File.WriteAllText(Path.Combine(migrations, "001_a.sql"), "A")

    match loadScriptsFromDirectory migrations with
    | Error e -> Assert.Fail e
    | Ok scripts ->
      Assert.Equal<string list>([ "001_a.sql"; "002_b.sql" ], scripts |> List.map fst)
      Assert.Equal<string list>([ "A"; "B" ], scripts |> List.map snd)
  finally
    try
      Directory.Delete(root, true)
    with _ ->
      ()
