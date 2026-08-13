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
let ``merges multiple mig ops lines in order`` () =
  withTempMigrations
    [ "001.sql",
      """
-- mig:rel User
-- mig:ops insert, select_by_id
-- mig:ops select_one_by(email), upsert
-- mig:ops delete_by_id
-- mig:bool active
CREATE TABLE app_user (
  id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
  email TEXT NOT NULL,
  active INTEGER NOT NULL
) STRICT;
""" ]
    (fun dir ->
      match parseMigrationsDirectory dir with
      | Error e -> Assert.Fail e
      | Ok anns ->
        Assert.Equal(1, anns.Length)
        let a = anns.Head
        Assert.Equal(Some "User", a.fsNameOverride)
        Assert.Equal(5, a.ops.Length)

        match a.ops with
        | [ Op.Insert; Op.SelectById; Op.SelectOneBy [ "email" ]; Op.Upsert; Op.DeleteById ] -> ()
        | other -> Assert.Fail $"unexpected ops order: {other}"

        Assert.Contains(a.overrides, fun o -> o.column = "active" && o.kind = ColumnOverrideKind.Bool))

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
let ``parses count op on table`` () =
  withTempMigrations
    [ "001.sql",
      """
-- mig:ops count, select_all
CREATE TABLE item (
  id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL
) STRICT;
""" ]
    (fun dir ->
      match parseMigrationsDirectory dir with
      | Error e -> Assert.Fail e
      | Ok anns ->
        match anns.Head.ops with
        | [ Op.Count; Op.SelectAll ] -> ()
        | other -> Assert.Fail $"unexpected ops: {other}")

[<Fact>]
let ``parses filter catalog and filter ops`` () =
  withTempMigrations
    [ "001.sql",
      """
-- mig:ops filter_search(created_at desc, id), filter_count, select_all
-- mig:filter status eq status
-- mig:filter label_prefix like_prefix label
-- mig:filter text_any eq_any label, notes
-- mig:filter min_score gte score
CREATE TABLE item (
  id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
  status TEXT NOT NULL,
  label TEXT NOT NULL,
  notes TEXT NOT NULL,
  score REAL NOT NULL,
  created_at TEXT NOT NULL
) STRICT;
""" ]
    (fun dir ->
      match parseMigrationsDirectory dir with
      | Error e -> Assert.Fail e
      | Ok anns ->
        let a = anns.Head

        match a.ops with
        | [ Op.FilterSearch orderBy; Op.FilterCount; Op.SelectAll ] ->
          match orderBy with
          | [ "created_at", SortDirection.Desc; "id", SortDirection.Asc ] -> ()
          | _ -> Assert.Fail $"unexpected orderBy: {orderBy}"
        | other -> Assert.Fail $"unexpected ops: {other}"

        Assert.Equal(4, a.filters.Length)

        Assert.Contains(a.filters, fun f -> f.name = "status" && f.kind = FilterKind.Eq && f.columns = [ "status" ])

        Assert.Contains(
          a.filters,
          fun f ->
            f.name = "label_prefix"
            && f.kind = FilterKind.LikePrefix
            && f.columns = [ "label" ]
        )

        Assert.Contains(
          a.filters,
          fun f ->
            f.name = "text_any"
            && f.kind = FilterKind.EqAny
            && f.columns = [ "label"; "notes" ]
        )

        Assert.Contains(a.filters, fun f -> f.name = "min_score" && f.kind = FilterKind.Gte && f.columns = [ "score" ]))

[<Fact>]
let ``parses in filter kind`` () =
  withTempMigrations
    [ "001.sql",
      """
-- mig:ops filter_search(game_pk), filter_count
-- mig:filter venue eq venue
-- mig:filter game_pks in game_pk
CREATE TABLE link (
  game_pk INTEGER NOT NULL,
  venue TEXT NOT NULL
) STRICT;
""" ]
    (fun dir ->
      match parseMigrationsDirectory dir with
      | Error e -> Assert.Fail e
      | Ok anns ->
        let a = anns.Head

        Assert.Contains(a.filters, fun f -> f.name = "game_pks" && f.kind = FilterKind.In && f.columns = [ "game_pk" ]))

[<Fact>]
let ``rejects in with two columns`` () =
  withTempMigrations
    [ "001.sql",
      """
-- mig:ops filter_count
-- mig:filter game_pks in game_pk, venue
CREATE TABLE link (
  game_pk INTEGER NOT NULL,
  venue TEXT NOT NULL
) STRICT;
""" ]
    (fun dir ->
      match parseMigrationsDirectory dir with
      | Ok _ -> Assert.Fail "expected error"
      | Error msg -> Assert.Contains("requires exactly one column", msg))

[<Fact>]
let ``rejects unknown filter kind`` () =
  withTempMigrations
    [ "001.sql",
      """
-- mig:ops filter_count
-- mig:filter status fuzzy status
CREATE TABLE item (
  id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
  status TEXT NOT NULL
) STRICT;
""" ]
    (fun dir ->
      match parseMigrationsDirectory dir with
      | Ok _ -> Assert.Fail "expected error"
      | Error msg -> Assert.Contains("unknown filter kind", msg))

[<Fact>]
let ``rejects eq_any with one column`` () =
  withTempMigrations
    [ "001.sql",
      """
-- mig:ops filter_count
-- mig:filter text_any eq_any label
CREATE TABLE item (
  id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
  label TEXT NOT NULL
) STRICT;
""" ]
    (fun dir ->
      match parseMigrationsDirectory dir with
      | Ok _ -> Assert.Fail "expected error"
      | Error msg -> Assert.Contains("eq_any requires at least two columns", msg))

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

[<Fact>]
let ``parses select_with and captures create SQL`` () =
  withTempMigrations
    [ "001.sql",
      """
-- mig:ops select_with(min_age, max_age)
CREATE VIEW student_age_range AS
  SELECT id, age FROM student
  WHERE age >= /*@min_age*/0 AND age < /*@max_age*/999;
""" ]
    (fun dir ->
      match parseMigrationsDirectory dir with
      | Error e -> Assert.Fail e
      | Ok anns ->
        Assert.Equal(1, anns.Length)

        match anns.Head.ops with
        | [ Op.SelectWith args ] -> Assert.Equal<string list>([ "min_age"; "max_age" ], args)
        | other -> Assert.Fail $"unexpected ops: {other}"

        match anns.Head.createSql with
        | None -> Assert.Fail "expected createSql"
        | Some sql ->
          Assert.Contains("/*@min_age*/0", sql)
          Assert.Contains("/*@max_age*/999", sql))
