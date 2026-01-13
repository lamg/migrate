module Test.NormalizedSchemaTest

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open migrate.CodeGen

[<Fact>]
let ``Detects extension table with correct naming and FK pattern`` () =
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
    return normalized
  }
  |> function
    | Ok [ n ] ->
      Assert.Equal("student", n.baseTable.name)
      Assert.Single n.extensions |> ignore
      Assert.Equal("student_address", n.extensions.[0].table.name)
      Assert.Equal("address", n.extensions.[0].aspectName)
      Assert.Equal("student_id", n.extensions.[0].fkColumn)
    | Ok [] -> Assert.Fail "Expected one normalized table to be detected"
    | Ok xs -> Assert.Fail $"Expected one normalized table, got {xs.Length}"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Detects multiple extension tables for same base table`` () =
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
    return normalized
  }
  |> function
    | Ok [ n ] ->
      Assert.Equal("student", n.baseTable.name)
      Assert.Equal(2, n.extensions.Length)

      let aspects = n.extensions |> List.map (fun e -> e.aspectName) |> Set.ofList
      Assert.Contains("address", aspects)
      Assert.Contains("email_phone", aspects)
    | Ok [] -> Assert.Fail "Expected one normalized table to be detected"
    | Ok xs -> Assert.Fail $"Expected one normalized table, got {xs.Length}"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Does not detect table with nullable columns as base table`` () =
  let sql =
    """
    CREATE TABLE student (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT,
      age INTEGER
    );

    CREATE TABLE student_address (
      student_id INTEGER PRIMARY KEY REFERENCES student(id),
      address TEXT NOT NULL
    );
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return normalized
  }
  |> function
    | Ok [] -> Assert.True true
    | Ok xs -> Assert.Fail $"Expected no normalized tables (base has nullable columns), got {xs.Length}"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Does not detect extension table with nullable columns`` () =
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

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return normalized
  }
  |> function
    | Ok [] -> Assert.True true
    | Ok xs -> Assert.Fail $"Expected no normalized tables (extension has nullable columns), got {xs.Length}"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Does not detect table without extension naming pattern`` () =
  let sql =
    """
    CREATE TABLE student (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL
    );

    CREATE TABLE address (
      id INTEGER PRIMARY KEY REFERENCES student(id),
      address TEXT NOT NULL
    );
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return normalized
  }
  |> function
    | Ok [] -> Assert.True true
    | Ok xs -> Assert.Fail $"Expected no normalized tables (wrong naming), got {xs.Length}"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Does not detect table with FK to different table`` () =
  let sql =
    """
    CREATE TABLE student (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL
    );

    CREATE TABLE course (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      title TEXT NOT NULL
    );

    CREATE TABLE student_enrollment (
      student_id INTEGER PRIMARY KEY REFERENCES course(id),
      enrolled_at TEXT NOT NULL
    );
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return normalized
  }
  |> function
    | Ok [] -> Assert.True true
    | Ok xs -> Assert.Fail $"Expected no normalized tables (FK to wrong table), got {xs.Length}"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Does not detect table without PK being FK`` () =
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

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return normalized
  }
  |> function
    | Ok [] -> Assert.True true
    | Ok xs -> Assert.Fail $"Expected no normalized tables (PK is not FK), got {xs.Length}"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``ClassifyTables separates normalized and regular tables`` () =
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

    CREATE TABLE course (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      title TEXT NOT NULL,
      description TEXT
    );
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let (normalized, regular) = NormalizedSchema.classifyTables parsed.tables
    return (normalized, regular)
  }
  |> function
    | Ok(normalized, regular) ->
      // student and student_address are normalized
      Assert.Single normalized |> ignore
      Assert.Equal("student", normalized.[0].baseTable.name)

      // course is regular (has nullable column)
      Assert.Single regular |> ignore
      Assert.Equal("course", regular.[0].name)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``hasNullableColumns returns true for table with nullable column`` () =
  let sql =
    "CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, age integer)"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return NormalizedSchema.hasNullableColumns table
  }
  |> function
    | Ok true -> Assert.True true
    | Ok false -> Assert.Fail "Expected hasNullableColumns to return true"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``hasNullableColumns returns false for table without nullable columns`` () =
  let sql =
    "CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, age integer NOT NULL)"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return NormalizedSchema.hasNullableColumns table
  }
  |> function
    | Ok false -> Assert.True true
    | Ok true -> Assert.Fail "Expected hasNullableColumns to return false"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Table level FK constraint is detected for extension tables`` () =
  let sql =
    """
    CREATE TABLE student (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL
    );

    CREATE TABLE student_address (
      student_id INTEGER PRIMARY KEY,
      address TEXT NOT NULL,
      FOREIGN KEY (student_id) REFERENCES student(id)
    );
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return normalized
  }
  |> function
    | Ok [ n ] ->
      Assert.Equal("student", n.baseTable.name)
      Assert.Single n.extensions |> ignore
      Assert.Equal("student_address", n.extensions.[0].table.name)
    | Ok [] -> Assert.Fail "Expected one normalized table with table-level FK"
    | Ok xs -> Assert.Fail $"Expected one normalized table, got {xs.Length}"
    | Error e -> Assert.Fail $"Parsing failed: {e}"
