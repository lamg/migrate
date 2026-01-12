module Test.CompositePKTest

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open migrate.CodeGen

[<Fact>]
let ``Composite primary key is parsed and recognized`` () =
  let sql =
    "CREATE TABLE enrollment(student_id integer NOT NULL, course_id integer NOT NULL, grade text, PRIMARY KEY(student_id, course_id))"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let pkCols = QueryGenerator.getPrimaryKey table
    return pkCols |> List.map (fun c -> c.name)
  }
  |> function
    | Ok [ "student_id"; "course_id" ] -> Assert.True true
    | Ok cols -> Assert.Fail $"Expected [student_id; course_id] but got {cols}"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Generated GetById method handles composite PK with transaction`` () =
  let sql =
    "CREATE TABLE enrollment(student_id integer NOT NULL, course_id integer NOT NULL, grade text, PRIMARY KEY(student_id, course_id))"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return QueryGenerator.generateGet table
  }
  |> function
    | Ok(Some code) ->
      Assert.Contains("tx: SqliteTransaction", code)
      Assert.Contains("student_id: int64", code)
      Assert.Contains("course_id: int64", code)
      Assert.Contains("WHERE student_id = @student_id AND course_id = @course_id", code)
      Assert.Contains("tx.Connection, tx", code)
    | Ok None -> Assert.Fail "GetById method should be generated for composite PK"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Generated Delete method handles composite PK with transaction`` () =
  let sql =
    "CREATE TABLE enrollment(student_id integer NOT NULL, course_id integer NOT NULL, grade text, PRIMARY KEY(student_id, course_id))"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return QueryGenerator.generateDelete table
  }
  |> function
    | Ok(Some code) ->
      Assert.Contains("tx: SqliteTransaction", code)
      Assert.Contains("student_id: int64", code)
      Assert.Contains("course_id: int64", code)
      Assert.Contains("WHERE student_id = @student_id AND course_id = @course_id", code)
      Assert.Contains("tx.Connection, tx", code)
    | Ok None -> Assert.Fail "Delete method should be generated for composite PK"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Generated Update method excludes all PK columns from SET with transaction`` () =
  let sql =
    "CREATE TABLE enrollment(student_id integer NOT NULL, course_id integer NOT NULL, grade text, PRIMARY KEY(student_id, course_id))"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return QueryGenerator.generateUpdate table
  }
  |> function
    | Ok(Some code) ->
      Assert.Contains("tx: SqliteTransaction", code)
      Assert.Contains("SET grade = @grade", code)
      Assert.DoesNotContain("SET student_id", code)
      Assert.DoesNotContain("SET course_id", code)
      Assert.Contains("WHERE student_id = @student_id AND course_id = @course_id", code)
      Assert.Contains("tx.Connection, tx", code)
    | Ok None -> Assert.Fail "Update method should be generated for composite PK"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``GetOne method is generated for tables`` () =
  let sql = "CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, age integer)"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let code = QueryGenerator.generateGetOne table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetOne (tx: SqliteTransaction)", code)
      Assert.Contains("SELECT id, name, age FROM student LIMIT 1", code)
      Assert.Contains("Result<Student option, SqliteException>", code)
      Assert.Contains("tx.Connection, tx", code)
    | Error e -> Assert.Fail $"Parsing failed: {e}"
