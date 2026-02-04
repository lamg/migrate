module Test.IgnoreNonUniqueTest

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open migrate.CodeGen

// ============================================================================
// SQL Comment Parsing Tests
// ============================================================================

[<Fact>]
let ``IgnoreNonUnique annotation is parsed from CREATE TABLE statement`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    -- IgnoreNonUnique
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return table.ignoreNonUniqueAnnotations
  }
  |> function
    | Ok annotations ->
      Assert.Equal(1, annotations.Length)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Table without IgnoreNonUnique annotation has empty list`` () =
  let sql = "CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return table.ignoreNonUniqueAnnotations
  }
  |> function
    | Ok annotations -> Assert.Empty(annotations)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``IgnoreNonUnique can coexist with QueryBy and QueryByOrCreate annotations`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    -- QueryBy(status)
    -- QueryByOrCreate(name)
    -- IgnoreNonUnique
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return (table.queryByAnnotations, table.queryByOrCreateAnnotations, table.ignoreNonUniqueAnnotations)
  }
  |> function
    | Ok(queryByAnnos, queryByOrCreateAnnos, ignoreAnnos) ->
      Assert.Single(queryByAnnos) |> ignore
      Assert.Single(queryByOrCreateAnnos) |> ignore
      Assert.Single(ignoreAnnos) |> ignore
    | Error e -> Assert.Fail $"Parsing failed: {e}"

// ============================================================================
// Code Generation Tests - Regular Tables
// ============================================================================

[<Fact>]
let ``IgnoreNonUnique generates InsertOrIgnore with INSERT OR IGNORE SQL`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    -- IgnoreNonUnique
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode false table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member InsertOrIgnore", code)
      Assert.Contains("INSERT OR IGNORE INTO student", code)
      Assert.Contains("Result<int64 option, SqliteException>", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``IgnoreNonUnique is not generated when annotation is absent`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode false table
    return code
  }
  |> function
    | Ok code ->
      Assert.DoesNotContain("InsertOrIgnore", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

// ============================================================================
// Code Generation Tests - Normalized Tables
// ============================================================================

[<Fact>]
let ``IgnoreNonUnique works with normalized tables`` () =
  let sql =
    """
    CREATE TABLE student (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
    -- IgnoreNonUnique
    CREATE TABLE student_address (id INTEGER PRIMARY KEY REFERENCES student(id), address TEXT NOT NULL);
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    let! code = NormalizedQueryGenerator.generateNormalizedTableCode false (normalized |> List.head)
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member InsertOrIgnore", code)
      Assert.Contains("(item: NewStudent)", code)
      Assert.Contains("INSERT OR IGNORE INTO student", code)
      Assert.Contains("INSERT INTO student_address", code)
      Assert.Contains("Result<int64 option, SqliteException>", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

// ============================================================================
// View Validation Tests
// ============================================================================

[<Fact>]
let ``IgnoreNonUnique on view fails validation`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);
    CREATE VIEW all_students AS SELECT id, name FROM student;
    -- IgnoreNonUnique
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let view = parsed.views |> List.head
    let! columns = ViewIntrospection.getViewColumns parsed.tables view
    let! code = QueryGenerator.generateViewCode false view columns
    return code
  }
  |> function
    | Ok _ -> Assert.Fail "Should have failed validation for view"
    | Error msg ->
      Assert.Contains("IgnoreNonUnique annotation is not supported on views", msg)
      Assert.Contains("read-only", msg)
