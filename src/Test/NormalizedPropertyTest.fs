module Test.NormalizedPropertyTest

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open migrate.CodeGen

[<Fact>]
let ``Properties are generated for common fields`` () =
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
      // Common fields (Id, Name) should have non-optional properties
      Assert.Contains("member this.Id : int64", code)
      Assert.Contains("member this.Name : string", code)

      // Pattern matching for common fields
      Assert.Contains("| Student.Base data -> data.Id", code)
      Assert.Contains("| Student.WithAddress data -> data.Id", code)
      Assert.Contains("| Student.Base data -> data.Name", code)
      Assert.Contains("| Student.WithAddress data -> data.Name", code)

    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Properties are generated for partial fields with option type`` () =
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
      // Partial field (Address) should have optional property
      Assert.Contains("member this.Address : string option", code)

      // Pattern matching for partial field
      Assert.Contains("| Student.Base _ -> None", code)
      Assert.Contains("| Student.WithAddress data -> Some data.Address", code)

    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Properties work with multiple extensions`` () =
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
    return normalized |> List.head |> NormalizedTypeGenerator.generateTypes
  }
  |> function
    | Ok code ->
      // Common fields
      Assert.Contains("member this.Id : int64", code)
      Assert.Contains("member this.Name : string", code)

      // Partial fields (only in specific extensions)
      Assert.Contains("member this.Address : string option", code)
      Assert.Contains("member this.Email : string option", code)
      Assert.Contains("member this.Phone : string option", code)

      // Address property should return None for Base and WithEmailPhone
      Assert.Contains("| Student.Base _ -> None", code)
      Assert.Contains("| Student.WithEmailPhone _ -> None", code)
      Assert.Contains("| Student.WithAddress data -> Some data.Address", code)

      // Email property pattern matching
      Assert.Contains("| Student.WithEmailPhone data -> Some data.Email", code)

    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Properties handle different types correctly`` () =
  let sql =
    """
    CREATE TABLE product (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL,
      price REAL NOT NULL
    );

    CREATE TABLE product_details (
      product_id INTEGER PRIMARY KEY REFERENCES product(id),
      description TEXT NOT NULL,
      weight REAL NOT NULL
    );
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let normalized = NormalizedSchema.detectNormalizedTables parsed.tables
    return normalized |> List.head |> NormalizedTypeGenerator.generateTypes
  }
  |> function
    | Ok code ->
      // Check types for common fields
      Assert.Contains("member this.Id : int64", code)
      Assert.Contains("member this.Name : string", code)
      Assert.Contains("member this.Price : float", code)

      // Check types for partial fields
      Assert.Contains("member this.Description : string option", code)
      Assert.Contains("member this.Weight : float option", code)

    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Property generation includes type extension syntax`` () =
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
      // Should include type extension
      Assert.Contains("type Student with", code)

      // Should have member syntax
      Assert.Contains("member this.", code)

    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Properties are not generated for NewType (insert type)`` () =
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
      // NewStudent type should be generated
      Assert.Contains("type NewStudent =", code)

      // But no properties on NewStudent
      Assert.DoesNotContain("type NewStudent with", code)

      // Properties should only be on Student (query type)
      Assert.Contains("type Student with", code)

    | Error e -> Assert.Fail $"Failed: {e}"

[<Fact>]
let ``Properties have correct pattern matching structure`` () =
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
      // Should have match expression
      Assert.Contains("match this with", code)

      // Should use qualified case names
      Assert.Contains("Student.Base", code)
      Assert.Contains("Student.WithAddress", code)

    | Error e -> Assert.Fail $"Failed: {e}"
