module Test.TransactionTest

open Xunit
open FsToolkit.ErrorHandling

open migrate.DeclarativeMigrations
open migrate.CodeGen

[<Fact>]
let ``WithTransaction method is generated`` () =
  let sql = "CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL)"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let code = QueryGenerator.generateWithTransaction table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member WithTransaction", code)
      Assert.Contains("conn: SqliteConnection", code)
      Assert.Contains("action: SqliteTransaction -> Result<'T, SqliteException>", code)
      Assert.Contains("BeginTransaction()", code)
      Assert.Contains("transaction.Commit()", code)
      Assert.Contains("transaction.Rollback()", code)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Insert with transaction overload is generated`` () =
  let sql = "CREATE TABLE student(id integer PRIMARY KEY AUTOINCREMENT, name text NOT NULL)"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let code = QueryGenerator.generateInsertWithTransaction table
    return code
  }
  |> function
    | Ok code ->
      Assert.Contains("static member Insert(conn: SqliteConnection, transaction: SqliteTransaction", code)
      Assert.Contains("new SqliteCommand(", code)
      Assert.Contains("conn, transaction)", code)
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Update with transaction overload is generated`` () =
  let sql = "CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL, email text)"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return QueryGenerator.generateUpdateWithTransaction table
  }
  |> function
    | Ok (Some code) ->
      Assert.Contains("static member Update(conn: SqliteConnection, transaction: SqliteTransaction", code)
      Assert.Contains("conn, transaction)", code)
      Assert.Contains("UPDATE student SET", code)
    | Ok None -> Assert.Fail "Update method should be generated"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Delete with transaction overload is generated`` () =
  let sql = "CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL)"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    return QueryGenerator.generateDeleteWithTransaction table
  }
  |> function
    | Ok (Some code) ->
      Assert.Contains("static member Delete(conn: SqliteConnection, transaction: SqliteTransaction", code)
      Assert.Contains("conn, transaction)", code)
      Assert.Contains("DELETE FROM student", code)
    | Ok None -> Assert.Fail "Delete method should be generated"
    | Error e -> Assert.Fail $"Parsing failed: {e}"

[<Fact>]
let ``Generated table code includes all transaction methods`` () =
  let sql = "CREATE TABLE student(id integer PRIMARY KEY, name text NOT NULL)"

  result {
    let! parsed = FParsecSqlParser.parseSqlFile ("test", sql)
    let table = parsed.tables |> List.head
    let code = QueryGenerator.generateTableCode table
    return code
  }
  |> function
    | Ok code ->
      // Standard methods
      Assert.Contains("static member Insert(conn: SqliteConnection, item:", code)
      Assert.Contains("static member GetById(", code)
      Assert.Contains("static member GetAll(", code)
      Assert.Contains("static member Update(conn: SqliteConnection, item:", code)
      Assert.Contains("static member Delete(conn: SqliteConnection, id:", code)
      // Transaction methods
      Assert.Contains("static member WithTransaction(", code)
      Assert.Contains("static member Insert(conn: SqliteConnection, transaction: SqliteTransaction", code)
      Assert.Contains("static member Update(conn: SqliteConnection, transaction: SqliteTransaction", code)
      Assert.Contains("static member Delete(conn: SqliteConnection, transaction: SqliteTransaction", code)
    | Error e -> Assert.Fail $"Parsing failed: {e}"
