module Test.QueryLikeTest

open Xunit
open FsToolkit.ErrorHandling
open FSharp.Text.Lexing

open migrate.DeclarativeMigrations
open migrate.CodeGen

let private parseSqlFile (fileName: string, sql: string) =
  try
    let lexbuf = LexBuffer<char>.FromString sql
    Ok(SqlParser.file SqlLexer.token lexbuf)
  with ex ->
    Error $"Parse error in {fileName}: {ex.Message}"

[<Fact>]
let ``QueryLike annotation is parsed from CREATE TABLE statement`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    -- QueryLike(name)
    """

  result {
    let! parsed = parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return table.queryLikeAnnotations
  }
  |> function
    | Ok annotations ->
      Assert.Single(annotations) |> ignore
      Assert.Equal<string list>([ "name" ], annotations.[0].columns)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``QueryLike generates LIKE query for regular tables`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    -- QueryLike(name)
    """

  result {
    let! parsed = parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode false table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetByNameLike", code)
      Assert.Contains("WHERE name LIKE '%' || @name || '%'", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryLike generates LIKE query for views`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    CREATE VIEW active_students AS SELECT id, name FROM student WHERE status = 'active';
    -- QueryLike(name)
    """

  result {
    let! parsed = parseSqlFile ("test", sql)
    let view = parsed.views |> List.head
    let! columns = ViewIntrospection.getViewColumns parsed.tables view
    let! code = QueryGenerator.generateViewCode false view columns
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetByNameLike", code)
      Assert.Contains("WHERE name LIKE '%' || @name || '%'", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryLike works with normalized tables`` () =
  let sql =
    """
    CREATE TABLE student (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
    -- QueryLike(name)
    CREATE TABLE student_address (id INTEGER PRIMARY KEY REFERENCES student(id), address TEXT NOT NULL);
    """

  result {
    let! parsed = parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    let! code = NormalizedQueryGenerator.generateNormalizedTableCode false (normalized |> List.head)
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetByNameLike", code)
      Assert.Contains("WHERE name LIKE '%' || @name || '%'", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryLike fails validation when multiple columns are provided`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    -- QueryLike(name, status)
    """

  result {
    let! parsed = parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! _ = QueryGenerator.generateTableCode false table
    return ()
  }
  |> function
    | Ok _ -> Assert.Fail "Should have failed validation"
    | Error msg -> Assert.Contains("supports exactly one column", msg)
