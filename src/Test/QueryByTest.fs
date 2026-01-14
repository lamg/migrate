module Test.QueryByTest

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open migrate.CodeGen

// ============================================================================
// SQL Comment Parsing Tests
// ============================================================================

[<Fact>]
let ``QueryBy annotation is parsed from CREATE TABLE statement`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    -- QueryBy(status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return table.queryByAnnotations
  }
  |> function
    | Ok annotations ->
      Assert.Equal(1, annotations.Length)
      Assert.Equal<string list>([ "status" ], annotations.[0].columns)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Multiple QueryBy annotations are parsed`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    -- QueryBy(status)
    -- QueryBy(name, status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return table.queryByAnnotations
  }
  |> function
    | Ok annotations ->
      Assert.Equal(2, annotations.Length)
      Assert.Equal<string list>([ "status" ], annotations.[0].columns)
      Assert.Equal<string list>([ "name"; "status" ], annotations.[1].columns)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``QueryBy annotation with single column is parsed`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);
    -- QueryBy(name)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return table.queryByAnnotations
  }
  |> function
    | Ok annotations ->
      Assert.Single(annotations) |> ignore
      Assert.Equal<string list>([ "name" ], annotations.[0].columns)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``QueryBy annotation with multiple columns is parsed`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text, grade integer);
    -- QueryBy(status, grade, name)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return table.queryByAnnotations
  }
  |> function
    | Ok annotations ->
      Assert.Single(annotations) |> ignore
      Assert.Equal<string list>([ "status"; "grade"; "name" ], annotations.[0].columns)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``QueryBy annotation works with views`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);
    CREATE VIEW all_students AS SELECT id, name FROM student;
    -- QueryBy(name)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let view = parsed.views |> List.head
    return view.queryByAnnotations
  }
  |> function
    | Ok annotations ->
      Assert.Single(annotations) |> ignore
      Assert.Equal<string list>([ "name" ], annotations.[0].columns)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Table without QueryBy annotation has empty list`` () =
  let sql = "CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return table.queryByAnnotations
  }
  |> function
    | Ok annotations -> Assert.Empty(annotations)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

// ============================================================================
// Code Generation Tests - Regular Tables
// ============================================================================

[<Fact>]
let ``QueryBy generates method with correct name for single column`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    -- QueryBy(status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetByStatus", code)
      Assert.Contains("status: string", code)
      Assert.Contains("tx: SqliteTransaction", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryBy generates method with tupled parameters for multiple columns`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    -- QueryBy(name, status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetByNameStatus", code)
      Assert.Contains("name: string", code)
      Assert.Contains("status: string", code)
      Assert.Contains("tx: SqliteTransaction", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryBy generates correct WHERE clause for single column`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, status text);
    -- QueryBy(status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("WHERE status = @status", code)
      Assert.Contains("@status", code)
      Assert.Contains("AddWithValue", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryBy generates correct WHERE clause for multiple columns`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text, status text);
    -- QueryBy(name, status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("WHERE name = @name AND status = @status", code)
      Assert.Contains("@name", code)
      Assert.Contains("@status", code)
      Assert.Contains("AddWithValue", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryBy generates multiple methods when multiple annotations present`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text, status text);
    -- QueryBy(status)
    -- QueryBy(name)
    -- QueryBy(name, status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetByStatus", code)
      Assert.Contains("static member GetByName", code)
      Assert.Contains("static member GetByNameStatus", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryBy returns Result of list type`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, status text);
    -- QueryBy(status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode table
    return code
  }
  |> function
    | Ok code -> Assert.Contains("Result<Student list, SqliteException>", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryBy handles nullable columns with option types`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, email text);
    -- QueryBy(email)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("email: string option", code)
      Assert.Contains("@email", code)
      Assert.Contains("match email with Some v -> box v | None -> box DBNull.Value", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

// ============================================================================
// Code Generation Tests - Normalized Tables
// ============================================================================

[<Fact>]
let ``QueryBy works with normalized tables`` () =
  let sql =
    """
    CREATE TABLE student (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
    -- QueryBy(name)
    CREATE TABLE student_address (id INTEGER PRIMARY KEY REFERENCES student(id), address TEXT NOT NULL);
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    let! code = normalized |> List.head |> NormalizedQueryGenerator.generateNormalizedTableCode
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetByName", code)
      Assert.Contains("name: string", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryBy with normalized table generates LEFT JOIN query`` () =
  let sql =
    """
    CREATE TABLE student (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
    -- QueryBy(name)
    CREATE TABLE student_address (id INTEGER PRIMARY KEY REFERENCES student(id), address TEXT NOT NULL);
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    let! code = normalized |> List.head |> NormalizedQueryGenerator.generateNormalizedTableCode
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("LEFT JOIN student_address", code)
      Assert.Contains("WHERE name = @name", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

// ============================================================================
// Code Generation Tests - Views
// ============================================================================

[<Fact>]
let ``QueryBy works with views`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    CREATE VIEW active_students AS SELECT id, name FROM student WHERE status = 'active';
    -- QueryBy(name)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let view = parsed.views |> List.head
    let! columns = ViewIntrospection.getViewColumns parsed.tables view
    let! code = QueryGenerator.generateViewCode view columns
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetByName", code)
      Assert.Contains("name: string", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryBy on view generates read-only methods`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);
    CREATE VIEW all_students AS SELECT id, name FROM student;
    -- QueryBy(name)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let view = parsed.views |> List.head
    let! columns = ViewIntrospection.getViewColumns parsed.tables view
    let! code = QueryGenerator.generateViewCode view columns
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetByName", code)
      // Views should not have Insert/Update/Delete
      Assert.DoesNotContain("Insert", code)
      Assert.DoesNotContain("Update", code)
      Assert.DoesNotContain("Delete", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

// ============================================================================
// Column Validation Tests
// ============================================================================

[<Fact>]
let ``QueryBy with invalid column name fails validation`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);
    -- QueryBy(status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode table
    return code
  }
  |> function
    | Ok _ -> Assert.Fail "Should have failed validation"
    | Error msg ->
      Assert.Contains("non-existent column 'status'", msg)
      Assert.Contains("Available columns: id, name", msg)

[<Fact>]
let ``QueryBy validation is case-insensitive`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, Name text NOT NULL);
    -- QueryBy(name)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode table
    return code
  }
  |> function
    | Ok code ->
      // Should succeed - case-insensitive matching
      Assert.Contains("static member GetByName", code)
    | Error e -> Assert.Fail $"Should have succeeded with case-insensitive matching: {e}"

[<Fact>]
let ``QueryBy with multiple invalid columns shows first error`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);
    -- QueryBy(status, grade)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode table
    return code
  }
  |> function
    | Ok _ -> Assert.Fail "Should have failed validation"
    | Error msg ->
      // Should report the first invalid column
      Assert.Contains("non-existent column", msg)

[<Fact>]
let ``QueryBy validation error for normalized tables`` () =
  let sql =
    """
    CREATE TABLE student (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
    -- QueryBy(invalid_column)
    CREATE TABLE student_address (id INTEGER PRIMARY KEY REFERENCES student(id), address TEXT NOT NULL);
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    let! code = normalized |> List.head |> NormalizedQueryGenerator.generateNormalizedTableCode
    return code
  }
  |> function
    | Ok _ -> Assert.Fail "Should have failed validation"
    | Error msg ->
      Assert.Contains("non-existent column 'invalid_column'", msg)
      Assert.Contains("Available columns:", msg)

[<Fact>]
let ``QueryBy validation error for views`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);
    CREATE VIEW all_students AS SELECT id, name FROM student;
    -- QueryBy(status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let view = parsed.views |> List.head
    let! columns = ViewIntrospection.getViewColumns parsed.tables view
    let! code = QueryGenerator.generateViewCode view columns
    return code
  }
  |> function
    | Ok _ -> Assert.Fail "Should have failed validation"
    | Error msg ->
      Assert.Contains("non-existent column 'status'", msg)
      Assert.Contains("Available columns: id, name", msg)
