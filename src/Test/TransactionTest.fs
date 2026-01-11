module Test.TransactionTest

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open migrate.CodeGen

[<Fact>]
let ``Insert method uses curried signature with tx last`` () =
  let sql =
    "CREATE TABLE student(id integer PRIMARY KEY AUTOINCREMENT, name text NOT NULL)"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let code = QueryGenerator.generateInsert table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member Insert (item: Student) (tx: SqliteTransaction)", code)
      Assert.Contains("tx.Connection, tx)", code)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``GetById method uses curried signature with tx last`` () =
  let sql = "CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL)"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return QueryGenerator.generateGet table
  }
  |> function
    | Ok(Some code) ->
      Assert.Contains("static member GetById (id: int64) (tx: SqliteTransaction)", code)
      Assert.Contains("tx.Connection, tx)", code)
    | Ok None -> Assert.Fail "GetById method should be generated"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``GetAll method uses curried signature`` () =
  let sql = "CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL)"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let code = QueryGenerator.generateGetAll table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member GetAll (tx: SqliteTransaction)", code)
      Assert.Contains("tx.Connection, tx)", code)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Update method uses curried signature with tx last`` () =
  let sql =
    "CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, email text)"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return QueryGenerator.generateUpdate table
  }
  |> function
    | Ok(Some code) ->
      Assert.Contains("static member Update (item: Student) (tx: SqliteTransaction)", code)
      Assert.Contains("tx.Connection, tx)", code)
    | Ok None -> Assert.Fail "Update method should be generated"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Delete method uses curried signature with tx last`` () =
  let sql = "CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL)"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return QueryGenerator.generateDelete table
  }
  |> function
    | Ok(Some code) ->
      Assert.Contains("static member Delete (id: int64) (tx: SqliteTransaction)", code)
      Assert.Contains("tx.Connection, tx)", code)
    | Ok None -> Assert.Fail "Delete method should be generated"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Generated table code uses curried signatures for all methods`` () =
  let sql = "CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL)"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let code = QueryGenerator.generateTableCode table
    return code
  }
  |> function
    | Ok code ->
      // All methods should use curried signatures with tx last
      Assert.Contains("Insert (item: Student) (tx: SqliteTransaction)", code)
      Assert.Contains("GetById (id: int64) (tx: SqliteTransaction)", code)
      Assert.Contains("GetAll (tx: SqliteTransaction)", code)
      Assert.Contains("Update (item: Student) (tx: SqliteTransaction)", code)
      Assert.Contains("Delete (id: int64) (tx: SqliteTransaction)", code)
      // WithTransaction should NOT be generated on the type
      Assert.DoesNotContain("WithTransaction", code)
    | Error e -> Assert.Fail $"Parsing failed: {e}"
