module Test.NormalizedSchemaValidationTest

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open migrate.CodeGen

[<Fact>]
let ``Validation fails when extension has nullable columns`` () =
  let sql =
    """
    CREATE TABLE student (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL
    );

    CREATE TABLE student_address (
      student_id INTEGER PRIMARY KEY REFERENCES student(id),
      address TEXT
    );
    """

  match FParsecSqlParser.parseSqlFile ("test", sql) with
  | Error e -> Assert.Fail $"Parse failed: {e}"
  | Ok parsed ->
    match NormalizedSchema.validateNormalizedTables parsed.tables with
    | Ok _ -> Assert.Fail "Should have failed validation due to nullable column"
    | Error errors ->
      Assert.NotEmpty errors

      match errors |> List.head with
      | Types.NullableColumnsDetected(table, columns) ->
        Assert.Equal("student_address", table)
        Assert.Contains("address", columns)
      | _ -> Assert.Fail "Expected NullableColumnsDetected error"

[<Fact>]
let ``Validation error message suggests adding NOT NULL`` () =
  let error = Types.NullableColumnsDetected("student_address", [ "address"; "phone" ])
  let message = NormalizedSchema.formatError error

  Assert.Contains("student_address", message)
  Assert.Contains("address", message)
  Assert.Contains("phone", message)
  Assert.Contains("NOT NULL", message)
  Assert.Contains("Suggestion:", message)

[<Fact>]
let ``Validation fails when FK column is not PK`` () =
  let sql =
    """
    CREATE TABLE student (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL
    );

    CREATE TABLE student_address (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      student_id INTEGER NOT NULL REFERENCES student(id),
      address TEXT NOT NULL
    );
    """

  match FParsecSqlParser.parseSqlFile ("test", sql) with
  | Error e -> Assert.Fail $"Parse failed: {e}"
  | Ok parsed ->
    match NormalizedSchema.validateNormalizedTables parsed.tables with
    | Ok _ -> Assert.Fail "Should have failed validation - FK is not PK"
    | Error errors ->
      Assert.NotEmpty errors

      match errors |> List.head with
      | Types.ForeignKeyNotPrimaryKey(table, fkCol) ->
        Assert.Equal("student_address", table)
        Assert.Contains("id", fkCol)
      | other -> Assert.Fail $"Expected ForeignKeyNotPrimaryKey error, got: {other}"

[<Fact>]
let ``Validation error message explains 1-1 relationship requirement`` () =
  let error = Types.ForeignKeyNotPrimaryKey("student_address", "student_id")
  let message = NormalizedSchema.formatError error

  Assert.Contains("student_address", message)
  Assert.Contains("student_id", message)
  Assert.Contains("PRIMARY KEY", message)
  Assert.Contains("1:1", message)
  Assert.Contains("Suggestion:", message)

[<Fact>]
let ``Validation fails when FK references wrong table`` () =
  let sql =
    """
    CREATE TABLE student (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL
    );

    CREATE TABLE teacher (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL
    );

    CREATE TABLE student_address (
      student_id INTEGER PRIMARY KEY REFERENCES teacher(id),
      address TEXT NOT NULL
    );
    """

  match FParsecSqlParser.parseSqlFile ("test", sql) with
  | Error e -> Assert.Fail $"Parse failed: {e}"
  | Ok parsed ->
    match NormalizedSchema.validateNormalizedTables parsed.tables with
    | Ok _ -> Assert.Fail "Should have failed validation - FK references wrong table"
    | Error errors ->
      Assert.NotEmpty errors

      let hasInvalidFkError =
        errors
        |> List.exists (fun e ->
          match e with
          | Types.InvalidForeignKey(ext, expected, reason) ->
            ext = "student_address" && expected = "student" && reason.Contains "teacher"
          | _ -> false)

      if not hasInvalidFkError then
        let errorMessages = errors |> List.map (fun e -> $"{e}") |> String.concat "; "
        Assert.Fail $"Expected InvalidForeignKey error, got: {errorMessages}"

[<Fact>]
let ``Validation error message shows FK mismatch`` () =
  let error =
    Types.InvalidForeignKey("student_address", "student", "FK references 'teacher' instead of 'student'")

  let message = NormalizedSchema.formatError error

  Assert.Contains("student_address", message)
  Assert.Contains("student", message)
  Assert.Contains("FOREIGN KEY", message)
  Assert.Contains("Suggestion:", message)

[<Fact>]
let ``Validation succeeds for valid normalized schema`` () =
  let sql =
    """
    CREATE TABLE student (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL
    );

    CREATE TABLE student_address (
      student_id INTEGER PRIMARY KEY REFERENCES student(id),
      address TEXT NOT NULL
    );
    """

  match FParsecSqlParser.parseSqlFile ("test", sql) with
  | Error e -> Assert.Fail $"Parse failed: {e}"
  | Ok parsed ->
    match NormalizedSchema.validateNormalizedTables parsed.tables with
    | Ok normalized ->
      Assert.NotEmpty normalized
      Assert.Equal("student", (List.head normalized).baseTable.name)
      Assert.Single((List.head normalized).extensions) |> ignore
    | Error errors ->
      let errorMessages =
        errors |> List.map NormalizedSchema.formatError |> String.concat "\n"

      Assert.Fail $"Validation should have succeeded, got errors:\n{errorMessages}"

[<Fact>]
let ``Validation collects multiple errors from different tables`` () =
  let sql =
    """
    CREATE TABLE student (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL
    );

    CREATE TABLE student_address (
      student_id INTEGER PRIMARY KEY REFERENCES student(id),
      address TEXT
    );

    CREATE TABLE student_email (
      student_id INTEGER PRIMARY KEY REFERENCES student(id),
      email TEXT
    );
    """

  match FParsecSqlParser.parseSqlFile ("test", sql) with
  | Error e -> Assert.Fail $"Parse failed: {e}"
  | Ok parsed ->
    match NormalizedSchema.validateNormalizedTables parsed.tables with
    | Ok _ -> Assert.Fail "Should have collected multiple validation errors"
    | Error errors ->
      Assert.True(errors.Length >= 2, $"Expected at least 2 errors, got {errors.Length}")

      let nullableErrors =
        errors
        |> List.filter (fun e ->
          match e with
          | Types.NullableColumnsDetected _ -> true
          | _ -> false)

      Assert.True(nullableErrors.Length >= 2, "Expected multiple nullable column errors")

[<Fact>]
let ``Validation allows tables with nullable columns to be skipped`` () =
  let sql =
    """
    CREATE TABLE student (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL,
      email TEXT
    );

    CREATE TABLE teacher (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL
    );

    CREATE TABLE teacher_address (
      teacher_id INTEGER PRIMARY KEY REFERENCES teacher(id),
      address TEXT NOT NULL
    );
    """

  match FParsecSqlParser.parseSqlFile ("test", sql) with
  | Error e -> Assert.Fail $"Parse failed: {e}"
  | Ok parsed ->
    match NormalizedSchema.validateNormalizedTables parsed.tables with
    | Ok normalized ->
      // student should be skipped (has nullable column), teacher should be detected
      Assert.Single(normalized) |> ignore
      Assert.Equal("teacher", (List.head normalized).baseTable.name)
    | Error errors ->
      let errorMessages =
        errors |> List.map NormalizedSchema.formatError |> String.concat "\n"

      Assert.Fail $"Should have succeeded (skipping student), got errors:\n{errorMessages}"

[<Fact>]
let ``formatError provides actionable suggestions for all error types`` () =
  let errors =
    [ Types.NullableColumnsDetected("table1", [ "col1"; "col2" ])
      Types.InvalidForeignKey("table2", "base", "some reason")
      Types.InvalidNaming("table3", "base")
      Types.ForeignKeyNotPrimaryKey("table4", "fk_col") ]

  for error in errors do
    let message = NormalizedSchema.formatError error
    // All errors should have a suggestion
    Assert.Contains("Suggestion:", message)
    // All errors should be multi-line for readability
    Assert.True(message.Contains "\n", $"Error message should be multi-line: {message}")
