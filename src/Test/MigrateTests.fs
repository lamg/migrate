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

let private itemTable =
  """
CREATE TABLE item (
  id INTEGER NOT NULL PRIMARY KEY,
  name TEXT NOT NULL
) STRICT;
"""

let private errorText =
  function
  | MigError.Message s -> s
  | e -> string e

[<Fact>]
let ``migrate bootstraps empty database from expected schema`` () =
  withTempDb (fun dbPath ->
    match await (migrate dbPath itemTable "") with
    | Error e -> Assert.Fail(errorText e)
    | Ok db ->
      Assert.Equal(dbPath, db.DbPath)

      match await (migrate dbPath itemTable "") with
      | Error e -> Assert.Fail("second migrate should be a no-op: " + errorText e)
      | Ok db2 -> Assert.Equal(dbPath, db2.DbPath))

[<Fact>]
let ``migrate applies hop then requires expected catalog`` () =
  withTempDb (fun dbPath ->
    let initial =
      """
CREATE TABLE item (
  id INTEGER NOT NULL PRIMARY KEY
) STRICT;
"""

    match await (migrate dbPath initial "") with
    | Error e -> Assert.Fail(errorText e)
    | Ok _ ->
      let hop = "ALTER TABLE item ADD COLUMN name TEXT NOT NULL DEFAULT '';"

      match await (migrate dbPath itemTable hop) with
      | Error e -> Assert.Fail(errorText e)
      | Ok _ ->
        match await (migrate dbPath itemTable hop) with
        | Error e -> Assert.Fail("idempotent hop: " + errorText e)
        | Ok db -> Assert.Equal(dbPath, db.DbPath))

[<Fact>]
let ``migrate errors when hop does not yield expected catalog`` () =
  withTempDb (fun dbPath ->
    let initial =
      """
CREATE TABLE item (
  id INTEGER NOT NULL PRIMARY KEY
) STRICT;
"""

    match await (migrate dbPath initial "") with
    | Error e -> Assert.Fail(errorText e)
    | Ok _ ->
      match await (migrate dbPath itemTable "ALTER TABLE item ADD COLUMN other TEXT;") with
      | Ok _ -> Assert.Fail "expected catalog mismatch"
      | Error e -> Assert.Contains("does not match expected schema", errorText e))

[<Fact>]
let ``migrate errors when hop is missing on a non-empty database`` () =
  withTempDb (fun dbPath ->
    match
      await (
        migrate
          dbPath
          """
CREATE TABLE item (
  id INTEGER NOT NULL PRIMARY KEY
) STRICT;
"""
          ""
      )
    with
    | Error e -> Assert.Fail(errorText e)
    | Ok _ ->
      match await (migrate dbPath itemTable "") with
      | Ok _ -> Assert.Fail "expected missing hop error"
      | Error e -> Assert.Contains("_migration.sql", errorText e))

[<Fact>]
let ``loadSchemaDirectory rejects missing directory`` () =
  match loadSchemaDirectory "/no/such/schema/dir" with
  | Ok _ -> Assert.Fail "expected error"
  | Error msg -> Assert.Contains("not found", msg)

[<Fact>]
let ``loadSchemaDirectory joins nested snapshot files and excludes root hop`` () =
  let root =
    Path.Combine(Path.GetTempPath(), "mig-load-" + Guid.NewGuid().ToString("N"))

  let schema = Path.Combine(root, "schema")
  Directory.CreateDirectory(Path.Combine(schema, "views")) |> ignore

  try
    File.WriteAllText(Path.Combine(schema, "b.sql"), "B")
    File.WriteAllText(Path.Combine(schema, "a.sql"), "A")
    File.WriteAllText(Path.Combine(schema, "views", "c.sql"), "C")
    File.WriteAllText(Path.Combine(schema, "_migration.sql"), "HOP")
    File.WriteAllText(Path.Combine(schema, "views", "_migration.sql"), "NESTED")

    match loadSchemaDirectory schema with
    | Error e -> Assert.Fail e
    | Ok loaded ->
      Assert.Equal("A\nB\nNESTED\nC", loaded.ExpectedSchema)
      Assert.Equal("HOP", loaded.Migration)
  finally
    try
      Directory.Delete(root, true)
    with _ ->
      ()
