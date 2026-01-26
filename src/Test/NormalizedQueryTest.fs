module Test.NormalizedQueryTest

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open migrate.CodeGen

[<Fact>]
let ``GetAll method is generated with correct signature`` () =
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

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return NormalizedQueryGenerator.generateGetAll false (normalized |> List.head)
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetAll (tx: SqliteTransaction) : Result<Student list, SqliteException> =", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``GetAll uses LEFT JOIN for extension tables`` () =
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

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return NormalizedQueryGenerator.generateGetAll false (normalized |> List.head)
  }
  |> function
    | Ok code -> Assert.Contains("LEFT JOIN student_address ext0 ON student.id = ext0.student_id", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``GetAll has case selection logic with NULL checks`` () =
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

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return NormalizedQueryGenerator.generateGetAll false (normalized |> List.head)
  }
  |> function
    | Ok code ->
      Assert.Contains("let hasAddress = not (reader.IsDBNull", code)
      Assert.Contains("match hasAddress with", code)
      Assert.Contains("| true ->", code)
      Assert.Contains("Student.WithAddress", code)
      Assert.Contains("| false ->", code)
      Assert.Contains("Student.Base", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``GetAll with multiple extensions generates proper pattern matching`` () =
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

    CREATE TABLE student_email_phone (
      student_id INTEGER PRIMARY KEY REFERENCES student(id),
      email TEXT NOT NULL,
      phone TEXT NOT NULL
    );
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return NormalizedQueryGenerator.generateGetAll false (normalized |> List.head)
  }
  |> function
    | Ok code ->
      Assert.Contains("let hasAddress = not (reader.IsDBNull", code)
      Assert.Contains("let hasEmailPhone = not (reader.IsDBNull", code)
      // Check for match statement (may have whitespace variations)
      Assert.Contains("match", code)
      Assert.Contains("hasAddress", code)
      Assert.Contains("hasEmailPhone", code)
      Assert.Contains("| true, false ->", code)
      Assert.Contains("Student.WithAddress", code)
      Assert.Contains("| false, true ->", code)
      Assert.Contains("Student.WithEmailPhone", code)
      Assert.Contains("| false, false ->", code)
      Assert.Contains("Student.Base", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``GetById method is generated with WHERE clause`` () =
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

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return NormalizedQueryGenerator.generateGetById false (normalized |> List.head)
  }
  |> function
    | Ok(Some code) ->
      Assert.Contains(
        "static member GetById (id: int64) (tx: SqliteTransaction) : Result<Student option, SqliteException> =",
        code
      )

      Assert.Contains("WHERE student.id = @id", code)
      Assert.Contains("cmd.Parameters.AddWithValue(\"@id\", id)", code)
    | Ok None -> Assert.Fail "GetById should be generated for table with PK"
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``GetById uses same LEFT JOIN and case selection as GetAll`` () =
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

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return NormalizedQueryGenerator.generateGetById false (normalized |> List.head)
  }
  |> function
    | Ok(Some code) ->
      Assert.Contains("LEFT JOIN student_address ext0 ON student.id = ext0.student_id", code)
      Assert.Contains("let hasAddress = not (reader.IsDBNull", code)
      Assert.Contains("Student.WithAddress", code)
      Assert.Contains("Student.Base", code)
    | Ok None -> Assert.Fail "GetById should be generated"
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``GetOne method is generated with LIMIT 1`` () =
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

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return NormalizedQueryGenerator.generateGetOne false (normalized |> List.head)
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetOne (tx: SqliteTransaction) : Result<Student option, SqliteException> =", code)
      Assert.Contains("LIMIT 1", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``generateNormalizedTableCode includes all query methods`` () =
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

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    let! code = NormalizedQueryGenerator.generateNormalizedTableCode false (normalized |> List.head)
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("type Student with", code)
      Assert.Contains("static member Insert", code)
      Assert.Contains("static member GetAll", code)
      Assert.Contains("static member GetById", code)
      Assert.Contains("static member GetOne", code)
    | Error e -> Assert.Fail $"Failed: {e}"
