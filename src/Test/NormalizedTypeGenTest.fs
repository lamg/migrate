module Test.NormalizedTypeGenTest

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open migrate.CodeGen

[<Fact>]
let ``Generates NewType DU with Base case`` () =
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
    return normalized |> List.head |> NormalizedTypeGenerator.generateNewType
  }
  |> function
    | Ok code ->
      Assert.Contains("[<RequireQualifiedAccess>]", code)
      Assert.Contains("type NewStudent =", code)
      Assert.Contains("| Base of {| Name: string |}", code)
      // Should NOT contain Id in NewStudent (auto-increment PK excluded)
      Assert.DoesNotContain("Id:", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Generates NewType DU with extension case`` () =
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
    return normalized |> List.head |> NormalizedTypeGenerator.generateNewType
  }
  |> function
    | Ok code -> Assert.Contains("| WithAddress of {| Name: string; Address: string |}", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Generates QueryType DU with Id in Base case`` () =
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
    return normalized |> List.head |> NormalizedTypeGenerator.generateQueryType
  }
  |> function
    | Ok code ->
      Assert.Contains("[<RequireQualifiedAccess>]", code)
      Assert.Contains("type Student =", code)
      Assert.Contains("| Base of {| Id: int64; Name: string |}", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Generates QueryType DU with Id in extension case`` () =
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
    return normalized |> List.head |> NormalizedTypeGenerator.generateQueryType
  }
  |> function
    | Ok code -> Assert.Contains("| WithAddress of {| Id: int64; Name: string; Address: string |}", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Generates multiple extension cases`` () =
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
    return normalized |> List.head |> NormalizedTypeGenerator.generateQueryType
  }
  |> function
    | Ok code ->
      Assert.Contains("| Base of", code)
      Assert.Contains("| WithAddress of", code)
      Assert.Contains("| WithEmailPhone of", code)
      Assert.Contains("Email: string", code)
      Assert.Contains("Phone: string", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``generateTypes produces both NewType and QueryType`` () =
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
    return normalized |> List.head |> NormalizedTypeGenerator.generateTypes
  }
  |> function
    | Ok code ->
      Assert.Contains("type NewStudent =", code)
      Assert.Contains("type Student =", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Extension FK column is excluded from extension case fields`` () =
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
    return normalized |> List.head |> NormalizedTypeGenerator.generateQueryType
  }
  |> function
    | Ok code ->
      // WithAddress should NOT contain StudentId (the FK column)
      Assert.DoesNotContain("StudentId:", code)
      // But should contain Id from base table
      Assert.Contains("Id: int64", code)
    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Handles table with multiple columns`` () =
  let sql =
    """
    CREATE TABLE person (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      first_name TEXT NOT NULL,
      last_name TEXT NOT NULL,
      age INTEGER NOT NULL
    );

    CREATE TABLE person_contact (
      person_id INTEGER PRIMARY KEY REFERENCES person(id),
      email TEXT NOT NULL,
      phone TEXT NOT NULL
    );
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return normalized |> List.head |> NormalizedTypeGenerator.generateTypes
  }
  |> function
    | Ok code ->
      // NewPerson Base case
      Assert.Contains("type NewPerson =", code)
      Assert.Contains("FirstName: string", code)
      Assert.Contains("LastName: string", code)
      Assert.Contains("Age: int64", code)

      // Person query type
      Assert.Contains("type Person =", code)

      // WithContact case should have all fields
      Assert.Contains("WithContact", code)
      Assert.Contains("Email: string", code)
      Assert.Contains("Phone: string", code)
    | Error e -> Assert.Fail $"Failed: {e}"
