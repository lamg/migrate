module AnnotationsTests

open System.IO
open Xunit
open MigLib.Codegen.Annotations
open MigLib.Codegen.Types

let private withTempMigrations (files: (string * string) list) (action: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), $"mig-ann-{System.Guid.NewGuid():N}")
  Directory.CreateDirectory dir |> ignore

  try
    for name, content in files do
      File.WriteAllText(Path.Combine(dir, name), content)

    action dir
  finally
    try
      Directory.Delete(dir, true)
    with _ ->
      ()

[<Fact>]
let ``parses mig ops and overrides`` () =
  withTempMigrations
    [ "001_users.sql",
      """
-- mig:rel User
-- mig:ops insert, select_by(email), select_by_id, upsert
-- mig:bool active
-- mig:datetime created_at
CREATE TABLE app_user (
  id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
  email TEXT NOT NULL,
  active INTEGER NOT NULL,
  created_at TEXT NOT NULL
) STRICT;
""" ]
    (fun dir ->
      match parseMigrationsDirectory dir with
      | Error e -> Assert.Fail e
      | Ok anns ->
        Assert.Single anns |> ignore
        let a = anns.Head
        Assert.Equal(Some "app_user", a.sqlName)
        Assert.Equal(Some "User", a.fsNameOverride)
        Assert.Equal(4, a.ops.Length)
        Assert.Contains(a.overrides, fun o -> o.column = "active" && o.kind = ColumnOverrideKind.Bool)
        Assert.Contains(a.overrides, fun o -> o.column = "created_at" && o.kind = ColumnOverrideKind.DateTime))

[<Fact>]
let ``derived name when mig rel omitted`` () =
  withTempMigrations
    [ "001.sql",
      """
-- mig:ops select_all
CREATE VIEW active_user AS SELECT 1 AS id;
""" ]
    (fun dir ->
      match parseMigrationsDirectory dir with
      | Error e -> Assert.Fail e
      | Ok anns ->
        Assert.Equal(Some "active_user", anns.Head.sqlName)
        Assert.True anns.Head.fsNameOverride.IsNone)

[<Fact>]
let ``rejects unknown op`` () =
  withTempMigrations
    [ "001.sql",
      """
-- mig:ops no_such_op
CREATE TABLE t (id INTEGER PRIMARY KEY);
""" ]
    (fun dir ->
      match parseMigrationsDirectory dir with
      | Ok _ -> Assert.Fail "expected error"
      | Error msg -> Assert.Contains("unknown op", msg))

[<Fact>]
let ``parses select_range with directions and defaults`` () =
  withTempMigrations
    [ "001.sql",
      """
-- mig:ops select_range(created_at desc, id), select_range(name)
CREATE TABLE event_log (
  id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
  created_at TEXT NOT NULL,
  name TEXT NOT NULL
) STRICT;
""" ]
    (fun dir ->
      match parseMigrationsDirectory dir with
      | Error e -> Assert.Fail e
      | Ok anns ->
        Assert.Equal(2, anns.Head.ops.Length)

        match anns.Head.ops with
        | [ Op.SelectRange order1; Op.SelectRange order2 ] ->
          match order1 with
          | [ "created_at", SortDirection.Desc; "id", SortDirection.Asc ] -> ()
          | _ -> Assert.Fail $"unexpected order1: {order1}"

          match order2 with
          | [ "name", SortDirection.Asc ] -> ()
          | _ -> Assert.Fail $"unexpected order2: {order2}"
        | other -> Assert.Fail $"unexpected ops: {other}")

[<Fact>]
let ``rejects invalid select_range direction`` () =
  withTempMigrations
    [ "001.sql",
      """
-- mig:ops select_range(created_at sideways)
CREATE TABLE t (id INTEGER PRIMARY KEY, created_at TEXT NOT NULL);
""" ]
    (fun dir ->
      match parseMigrationsDirectory dir with
      | Ok _ -> Assert.Fail "expected error"
      | Error msg -> Assert.Contains("invalid select_range direction", msg))

[<Fact>]
let ``rejects empty select_range`` () =
  withTempMigrations
    [ "001.sql",
      """
-- mig:ops select_range()
CREATE TABLE t (id INTEGER PRIMARY KEY);
""" ]
    (fun dir ->
      match parseMigrationsDirectory dir with
      | Ok _ -> Assert.Fail "expected error"
      | Error msg -> Assert.Contains("at least one order column", msg))
