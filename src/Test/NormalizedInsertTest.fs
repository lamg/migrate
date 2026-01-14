module Test.NormalizedInsertTest

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open migrate.CodeGen

[<Fact>]
let ``Insert method is generated with correct signature`` () =
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
    return normalized |> List.head |> NormalizedQueryGenerator.generateInsert
  }
  |> function
    | Ok code ->
      Assert.Contains("static member Insert (item: NewStudent) (tx: SqliteTransaction)", code)
      Assert.Contains(": Result<int64, SqliteException> =", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Insert method has pattern matching on NewType cases`` () =
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
    return normalized |> List.head |> NormalizedQueryGenerator.generateInsert
  }
  |> function
    | Ok code ->
      Assert.Contains("match item with", code)
      Assert.Contains("| NewStudent.Base data ->", code)
      Assert.Contains("| NewStudent.WithAddress data ->", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Base case does single INSERT into base table`` () =
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
    return normalized |> List.head |> NormalizedQueryGenerator.generateInsert
  }
  |> function
    | Ok code ->
      // Base case should have single INSERT
      Assert.Contains("INSERT INTO student (name) VALUES (@name)", code)
      Assert.Contains("data.Name", code)
      // Should get last_insert_rowid
      Assert.Contains("SELECT last_insert_rowid()", code)
      Assert.Contains("Ok studentId", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Extension case does two INSERTs in transaction`` () =
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
    return normalized |> List.head |> NormalizedQueryGenerator.generateInsert
  }
  |> function
    | Ok code ->
      // Extension case should have two INSERTs
      Assert.Contains("use cmd1 = new SqliteCommand(\"INSERT INTO student", code)
      Assert.Contains("use cmd2 = new SqliteCommand(\"INSERT INTO student_address", code)
      // Should insert FK with ID from base table
      Assert.Contains("student_id", code)
      Assert.Contains("@student_id\", studentId", code)
      // Should insert extension columns
      Assert.Contains("address", code)
      Assert.Contains("data.Address", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Multiple extension cases are generated`` () =
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
    return normalized |> List.head |> NormalizedQueryGenerator.generateInsert
  }
  |> function
    | Ok code ->
      Assert.Contains("| NewStudent.Base data ->", code)
      Assert.Contains("| NewStudent.WithAddress data ->", code)
      Assert.Contains("| NewStudent.WithEmailPhone data ->", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Insert has try-catch with SqliteException`` () =
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
    return normalized |> List.head |> NormalizedQueryGenerator.generateInsert
  }
  |> function
    | Ok code ->
      Assert.Contains("try", code)
      Assert.Contains("with", code)
      Assert.Contains(":? SqliteException as ex -> Error ex", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``generateNormalizedTableCode produces type extension`` () =
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
    let! code = normalized |> List.head |> NormalizedQueryGenerator.generateNormalizedTableCode
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("type Student with", code)
      Assert.Contains("static member Insert", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Extension case excludes FK column from extension INSERT`` () =
  let sql =
    """
    CREATE TABLE student (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL
    );

    CREATE TABLE student_address (
      student_id INTEGER PRIMARY KEY REFERENCES student(id),
      address TEXT NOT NULL,
      city TEXT NOT NULL
    );
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return normalized |> List.head |> NormalizedQueryGenerator.generateInsert
  }
  |> function
    | Ok code ->
      // FK column should be in the INSERT but bound separately, not from data
      Assert.Contains("INSERT INTO student_address (student_id, address, city)", code)
      Assert.Contains("@student_id\", studentId", code)
      // Extension fields should come from data
      Assert.Contains("data.Address", code)
      Assert.Contains("data.City", code)
    | Error e -> Assert.Fail $"Failed: {e}"
