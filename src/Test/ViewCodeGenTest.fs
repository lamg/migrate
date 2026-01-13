module Test.ViewCodeGenTest

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open migrate.CodeGen

[<Fact>]
let ``View type generation includes columns`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, age integer);
    CREATE VIEW adult_students AS SELECT id, name FROM student WHERE age >= 18;
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let view = parsed.views |> List.head
    let! columns = ViewIntrospection.getViewColumns parsed.tables view
    let code = TypeGenerator.generateViewRecordType view.name columns
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("type AdultStudents =", code)
      Assert.Contains("Id:", code)
      Assert.Contains("Name:", code)
      Assert.DoesNotContain("Age:", code) // Age is not in the view
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``View GetAll method is generated`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);
    CREATE VIEW all_students AS SELECT id, name FROM student;
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let view = parsed.views |> List.head
    let! columns = ViewIntrospection.getViewColumns parsed.tables view
    let code = QueryGenerator.generateViewCode view.name columns
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("type AllStudents with", code)
      Assert.Contains("static member GetAll (tx: SqliteTransaction)", code)
      Assert.Contains("SELECT id, name FROM all_students", code)
      Assert.DoesNotContain("Insert", code) // Views are read-only
      Assert.DoesNotContain("Update", code)
      Assert.DoesNotContain("Delete", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``View with nullable columns generates option types`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, email text);
    CREATE VIEW students_with_email AS SELECT id, name, email FROM student WHERE email IS NOT NULL;
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let view = parsed.views |> List.head
    let! columns = ViewIntrospection.getViewColumns parsed.tables view
    let code = TypeGenerator.generateViewRecordType view.name columns
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("Name: string", code) // NOT NULL column
      Assert.Contains("Email: string option", code) // Nullable column
    | Error e -> Assert.Fail $"Code generation failed: {e}"

[<Fact>]
let ``Complex view with JOIN is supported`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);
    CREATE TABLE course(id integer PRIMARY KEY, title text NOT NULL);
    CREATE TABLE enrollment(student_id integer, course_id integer);
    CREATE VIEW student_courses AS
      SELECT s.name, c.title
      FROM student s
      JOIN enrollment e ON s.id = e.student_id
      JOIN course c ON e.course_id = c.id;
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let view = parsed.views |> List.head
    let! columns = ViewIntrospection.getViewColumns parsed.tables view
    return columns.Length
  }
  |> function
    | Ok count -> Assert.Equal(2, count) // name and title columns
    | Error e -> Assert.Fail $"View introspection failed: {e}"

[<Fact>]
let ``View GetOne method is generated`` () =
  let sql =
    """
    CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL);
    CREATE VIEW all_students AS SELECT id, name FROM student;
    """

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let view = parsed.views |> List.head
    let! columns = ViewIntrospection.getViewColumns parsed.tables view
    let code = QueryGenerator.generateViewCode view.name columns
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetOne (tx: SqliteTransaction)", code)
      Assert.Contains("SELECT id, name FROM all_students LIMIT 1", code)
      Assert.Contains("Result<AllStudents option, SqliteException>", code)
    | Error e -> Assert.Fail $"Code generation failed: {e}"
