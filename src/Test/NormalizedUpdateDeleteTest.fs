module Test.NormalizedUpdateDeleteTest

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open migrate.CodeGen

[<Fact>]
let ``Update method is generated with correct signature`` () =
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
    let! parsed = SqlParserWrapper.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return NormalizedQueryGenerator.generateUpdate false (normalized |> List.head)
  }
  |> function
    | Ok(Some code) ->
      Assert.Contains("static member Update (item: Student) (tx: SqliteTransaction)", code)
      Assert.Contains(": Result<unit, SqliteException> =", code)
    | Ok None -> Assert.Fail "Update should be generated for table with PK"
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Update method has pattern matching on Student cases`` () =
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
    let! parsed = SqlParserWrapper.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return NormalizedQueryGenerator.generateUpdate false (normalized |> List.head)
  }
  |> function
    | Ok(Some code) ->
      Assert.Contains("match item with", code)
      Assert.Contains("| Student.Base(", code)
      Assert.Contains("| Student.WithAddress(", code)
    | Ok None -> Assert.Fail "Update should be generated"
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Update Base case updates base and deletes extensions`` () =
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
    let! parsed = SqlParserWrapper.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return NormalizedQueryGenerator.generateUpdate false (normalized |> List.head)
  }
  |> function
    | Ok(Some code) ->
      // Base case should UPDATE base table
      Assert.Contains("UPDATE student SET name = @name WHERE id = @id", code)
      // Base case should DELETE from extension tables
      Assert.Contains("DELETE FROM student_address WHERE student_id = @id", code)
    | Ok None -> Assert.Fail "Update should be generated"
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Update extension case uses INSERT OR REPLACE`` () =
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
    let! parsed = SqlParserWrapper.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return NormalizedQueryGenerator.generateUpdate false (normalized |> List.head)
  }
  |> function
    | Ok(Some code) ->
      // Extension case should UPDATE base
      Assert.Contains("UPDATE student SET name = @name WHERE id = @id", code)
      // Extension case should INSERT OR REPLACE extension
      Assert.Contains("INSERT OR REPLACE INTO student_address", code)
      Assert.Contains("student_id, address", code)
    | Ok None -> Assert.Fail "Update should be generated"
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Update with multiple extensions deletes other extensions`` () =
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
    let! parsed = SqlParserWrapper.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return NormalizedQueryGenerator.generateUpdate false (normalized |> List.head)
  }
  |> function
    | Ok(Some code) ->
      // When updating WithAddress, should delete WithEmailPhone
      Assert.Contains("DELETE FROM student_email_phone", code)
      // When updating WithEmailPhone, should delete WithAddress
      Assert.Contains("DELETE FROM student_address", code)
    | Ok None -> Assert.Fail "Update should be generated"
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Delete method is generated with correct signature`` () =
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
    let! parsed = SqlParserWrapper.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return NormalizedQueryGenerator.generateDelete false (normalized |> List.head)
  }
  |> function
    | Ok(Some code) ->
      Assert.Contains("static member Delete (id: int64) (tx: SqliteTransaction)", code)
      Assert.Contains(": Result<unit, SqliteException> =", code)
    | Ok None -> Assert.Fail "Delete should be generated for table with PK"
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Delete method only deletes from base table`` () =
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
    let! parsed = SqlParserWrapper.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return NormalizedQueryGenerator.generateDelete false (normalized |> List.head)
  }
  |> function
    | Ok(Some code) ->
      // Delete should only target base table (FK CASCADE handles extensions)
      Assert.Contains("DELETE FROM student WHERE id = @id", code)
      // Should NOT explicitly delete from extension tables
      Assert.DoesNotContain("DELETE FROM student_address", code)
    | Ok None -> Assert.Fail "Delete should be generated"
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``generateNormalizedTableCode includes Update and Delete`` () =
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
    let! parsed = SqlParserWrapper.parseSqlFile ("test", sql)
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
      Assert.Contains("static member Update", code)
      Assert.Contains("static member Delete", code)
    | Error e -> Assert.Fail $"Failed: {e}"
