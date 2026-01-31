module Test.QueryByOrCreateTest

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open migrate.CodeGen

// ============================================================================
// SQL Comment Parsing Tests
// ============================================================================

[<Fact>]
let ``QueryByOrCreate annotation is parsed from CREATE TABLE statement`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    -- QueryByOrCreate(status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return table.queryByOrCreateAnnotations
  }
  |> function
    | Ok annotations ->
      Assert.Equal(1, annotations.Length)
      Assert.Equal<string list>([ "status" ], annotations.[0].columns)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Multiple QueryByOrCreate annotations are parsed`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    -- QueryByOrCreate(status)
    -- QueryByOrCreate(name, status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return table.queryByOrCreateAnnotations
  }
  |> function
    | Ok annotations ->
      Assert.Equal(2, annotations.Length)
      Assert.Equal<string list>([ "status" ], annotations.[0].columns)
      Assert.Equal<string list>([ "name"; "status" ], annotations.[1].columns)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``QueryByOrCreate annotation with single column is parsed`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);
    -- QueryByOrCreate(name)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return table.queryByOrCreateAnnotations
  }
  |> function
    | Ok annotations ->
      Assert.Single(annotations) |> ignore
      Assert.Equal<string list>([ "name" ], annotations.[0].columns)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``QueryByOrCreate annotation with multiple columns is parsed`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text, grade integer);
    -- QueryByOrCreate(status, grade, name)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return table.queryByOrCreateAnnotations
  }
  |> function
    | Ok annotations ->
      Assert.Single(annotations) |> ignore
      Assert.Equal<string list>([ "status"; "grade"; "name" ], annotations.[0].columns)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Table without QueryByOrCreate annotation has empty list`` () =
  let sql = "CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return table.queryByOrCreateAnnotations
  }
  |> function
    | Ok annotations -> Assert.Empty(annotations)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``QueryBy and QueryByOrCreate annotations can coexist`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    -- QueryBy(status)
    -- QueryByOrCreate(name)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return (table.queryByAnnotations, table.queryByOrCreateAnnotations)
  }
  |> function
    | Ok(queryByAnnos, queryByOrCreateAnnos) ->
      Assert.Single(queryByAnnos) |> ignore
      Assert.Single(queryByOrCreateAnnos) |> ignore
      Assert.Equal<string list>([ "status" ], queryByAnnos.[0].columns)
      Assert.Equal<string list>([ "name" ], queryByOrCreateAnnos.[0].columns)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

// ============================================================================
// Code Generation Tests - Regular Tables
// ============================================================================

[<Fact>]
let ``QueryByOrCreate generates method with correct name for single column`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    -- QueryByOrCreate(status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode false table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetByStatusOrCreate", code)
      Assert.Contains("(newItem: Student)", code)
      Assert.Contains("tx: SqliteTransaction", code)
      // Should extract value from newItem
      Assert.Contains("let status = newItem.Status", code)
      // Should NOT have separate status parameter
      Assert.DoesNotContain("status: string, newItem", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryByOrCreate generates method with tupled parameters for multiple columns`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, status text);
    -- QueryByOrCreate(name, status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode false table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetByNameStatusOrCreate", code)
      Assert.Contains("(newItem: Student)", code)
      Assert.Contains("tx: SqliteTransaction", code)
      // Should extract multiple values from newItem
      Assert.Contains("let name = newItem.Name", code)
      Assert.Contains("let status = newItem.Status", code)
      // Should NOT have tuple parameters
      Assert.DoesNotContain("(name, status):", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryByOrCreate generates correct WHERE clause for single column`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, status text);
    -- QueryByOrCreate(status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode false table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("WHERE status = @status LIMIT 1", code)
      Assert.Contains("@status", code)
      Assert.Contains("AddWithValue", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryByOrCreate generates correct WHERE clause for multiple columns`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text, status text);
    -- QueryByOrCreate(name, status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode false table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("WHERE name = @name AND status = @status LIMIT 1", code)
      Assert.Contains("@name", code)
      Assert.Contains("@status", code)
      Assert.Contains("AddWithValue", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryByOrCreate returns Result of single item type`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, status text);
    -- QueryByOrCreate(status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode false table
    return code
  }
  |> function
    | Ok code ->
      // Check that QueryByOrCreate method returns single item, not list or option
      Assert.Contains("static member GetByStatusOrCreate", code)
      Assert.Contains("Result<Student, SqliteException>", code)
      // Make sure the OrCreate method specifically doesn't have list/option in its signature
      let methodStart = code.IndexOf("GetByStatusOrCreate")
      let nextMethod = code.IndexOf("static member", methodStart + 1)

      let methodCode =
        if nextMethod = -1 then
          code.Substring(methodStart)
        else
          code.Substring(methodStart, nextMethod - methodStart)

      Assert.DoesNotContain("Student list", methodCode)
      Assert.DoesNotContain("Student option", methodCode)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryByOrCreate handles nullable columns with option types`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, email text);
    -- QueryByOrCreate(email)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode false table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("(newItem: Student)", code)
      Assert.Contains("let email = newItem.Email", code)
      Assert.Contains("@email", code)
      // After AST migration, match expressions are formatted across multiple lines
      Assert.Contains("match email with", code)
      Assert.Contains("Some v -> box v", code)
      Assert.Contains("None -> box DBNull.Value", code)
      // Should NOT have email as separate parameter
      Assert.DoesNotContain("email: string option, newItem", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryByOrCreate includes Insert fallback logic`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);
    -- QueryByOrCreate(name)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode false table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("Student.Insert newItem tx", code)
      Assert.Contains("Student.GetById", code)
      // Comments are not preserved in AST-generated code
      Assert.Contains("reader.Close()", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryByOrCreate generates multiple methods when multiple annotations present`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text, status text);
    -- QueryByOrCreate(status)
    -- QueryByOrCreate(name)
    -- QueryByOrCreate(name, status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode false table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetByStatusOrCreate", code)
      Assert.Contains("static member GetByNameOrCreate", code)
      Assert.Contains("static member GetByNameStatusOrCreate", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

// ============================================================================
// Code Generation Tests - Normalized Tables
// ============================================================================

[<Fact>]
let ``QueryByOrCreate works with normalized tables`` () =
  let sql =
    """
    CREATE TABLE student (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
    -- QueryByOrCreate(name)
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
      Assert.Contains("static member GetByNameOrCreate", code)
      Assert.Contains("(newItem: NewStudent)", code)
      // Should extract name from NewStudent DU via positional pattern matching
      Assert.Contains("match newItem with", code)
      // Base case has 'id, name' fields (id is not auto-increment) - extract name with wildcard for id
      Assert.Contains("NewStudent.Base(_, name) -> name", code)
      // WithAddress case has 'id, name, address' - extract name with wildcards for id and address
      Assert.Contains("NewStudent.WithAddress(_, name, _) -> name", code)
      // Should NOT have name as separate parameter
      Assert.DoesNotContain("name: string, newItem", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryByOrCreate with normalized table uses NewType for insert`` () =
  let sql =
    """
    CREATE TABLE student (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
    -- QueryByOrCreate(name)
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
      Assert.Contains("newItem: NewStudent", code)
      Assert.Contains("Student.Insert newItem tx", code)
      Assert.Contains("Result<Student, SqliteException>", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``QueryByOrCreate with normalized table generates LEFT JOIN query`` () =
  let sql =
    """
    CREATE TABLE student (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
    -- QueryByOrCreate(name)
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
      Assert.Contains("LEFT JOIN student_address", code)
      Assert.Contains("WHERE name = @name LIMIT 1", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

// ============================================================================
// View Validation Tests
// ============================================================================

[<Fact>]
let ``QueryByOrCreate on view fails validation`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);
    CREATE VIEW all_students AS SELECT id, name FROM student;
    -- QueryByOrCreate(name)
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
      Assert.Contains("QueryByOrCreate annotation is not supported on views", msg)
      Assert.Contains("read-only", msg)

// ============================================================================
// Column Validation Tests
// ============================================================================

[<Fact>]
let ``QueryByOrCreate with invalid column name fails validation`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);
    -- QueryByOrCreate(status)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode false table
    return code
  }
  |> function
    | Ok _ -> Assert.Fail "Should have failed validation"
    | Error msg ->
      Assert.Contains("non-existent column 'status'", msg)
      Assert.Contains("Available columns: id, name", msg)

[<Fact>]
let ``QueryByOrCreate validation is case-insensitive`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, Name text NOT NULL);
    -- QueryByOrCreate(name)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode false table
    return code
  }
  |> function
    | Ok code ->
      // Should succeed - case-insensitive matching
      Assert.Contains("static member GetByNameOrCreate", code)
    | Error e -> Assert.Fail $"Should have succeeded with case-insensitive matching: {e}"

[<Fact>]
let ``QueryByOrCreate with multiple invalid columns shows first error`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);
    -- QueryByOrCreate(status, grade)
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let! code = QueryGenerator.generateTableCode false table
    return code
  }
  |> function
    | Ok _ -> Assert.Fail "Should have failed validation"
    | Error msg ->
      // Should report the first invalid column
      Assert.Contains("non-existent column", msg)

[<Fact>]
let ``QueryByOrCreate validation error for normalized tables`` () =
  let sql =
    """
    CREATE TABLE student (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
    -- QueryByOrCreate(invalid_column)
    CREATE TABLE student_address (id INTEGER PRIMARY KEY REFERENCES student(id), address TEXT NOT NULL);
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    let! code = NormalizedQueryGenerator.generateNormalizedTableCode false (normalized |> List.head)
    return code
  }
  |> function
    | Ok _ -> Assert.Fail "Should have failed validation"
    | Error msg ->
      Assert.Contains("non-existent column 'invalid_column'", msg)
      Assert.Contains("Available columns:", msg)
