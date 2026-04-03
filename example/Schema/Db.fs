module Db

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Mig.DeclarativeMigrations.Types
open Mig.HotMigration
open MigLib.Db

[<Literal>]
let DbFile = "Schema-6dc5755fde7d9df3.sqlite"

[<Literal>]
let SchemaHash = "6dc5755fde7d9df3"

let SchemaIdentity: SchemaIdentity =
  { schemaHash = SchemaHash
    schemaCommit = None }

let Schema: SqlFile =
  { measureTypes = []
    inserts = []
    views = []
    tables =
      [ { name = "student"
          previousName = None
          dropColumns = []
          columns =
            [ { name = "id"
                previousName = None
                columnType = SqlType.SqlInteger
                constraints =
                  [ ColumnConstraint.NotNull
                    ColumnConstraint.PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                enumLikeDu = None
                unitOfMeasure = None }
              { name = "name"
                previousName = None
                columnType = SqlType.SqlText
                constraints = [ ColumnConstraint.NotNull; ColumnConstraint.Unique [] ]
                enumLikeDu = None
                unitOfMeasure = None }
              { name = "age"
                previousName = None
                columnType = SqlType.SqlInteger
                constraints = [ ColumnConstraint.NotNull; ColumnConstraint.Default(Expr.Integer 18) ]
                enumLikeDu = None
                unitOfMeasure = None } ]
          constraints = []
          queryByAnnotations = [ { columns = [ "name" ] } ]
          queryLikeAnnotations = []
          queryByOrCreateAnnotations = []
          insertOrIgnoreAnnotations = []
          deleteAllAnnotations = [ DeleteAllAnnotation ]
          upsertAnnotations = [] } ]
    indexes = []
    triggers = [] }

type Student = { Id: int64; Name: string; Age: int64 }


type Student with
  static member Insert (item: Student) (tx: SqliteTransaction) : Task<Result<int64, SqliteException>> =
    executeInsert
      "INSERT INTO student (name, age) VALUES (@name, @age)"
      (fun cmd ->
        cmd.Parameters.AddWithValue("@name", item.Name) |> ignore
        cmd.Parameters.AddWithValue("@age", item.Age) |> ignore)
      tx
      (fun newId ->
        task {
          MigrationLog.recordInsert tx "student" [ "name", box item.Name; "age", box item.Age; "id", box newId ]
          return Ok newId
        })

  static member SelectById (id: int64) (tx: SqliteTransaction) : Task<Result<Student option, SqliteException>> =
    querySingle
      "SELECT id, name, age FROM student WHERE id = @id"
      (fun cmd -> cmd.Parameters.AddWithValue("@id", id) |> ignore)
      (fun reader ->
        { Id = reader.GetInt64 0
          Name = reader.GetString 1
          Age = reader.GetInt64 2 })
      tx

  static member SelectAll(tx: SqliteTransaction) : Task<Result<Student list, SqliteException>> =
    queryList
      "SELECT id, name, age FROM student"
      (fun _ -> ())
      (fun reader ->
        { Id = reader.GetInt64 0
          Name = reader.GetString 1
          Age = reader.GetInt64 2 })
      tx

  static member SelectOne(tx: SqliteTransaction) : Task<Result<Student option, SqliteException>> =
    querySingle
      "SELECT id, name, age FROM student LIMIT 1"
      (fun _ -> ())
      (fun reader ->
        { Id = reader.GetInt64 0
          Name = reader.GetString 1
          Age = reader.GetInt64 2 })
      tx

  static member Update (item: Student) (tx: SqliteTransaction) : Task<Result<unit, SqliteException>> =
    task {
      let! updateResult =
        executeWriteUnit
          "UPDATE student SET name = @name, age = @age WHERE id = @id"
          (fun cmd ->
            cmd.Parameters.AddWithValue("@id", item.Id) |> ignore
            cmd.Parameters.AddWithValue("@name", item.Name) |> ignore
            cmd.Parameters.AddWithValue("@age", item.Age) |> ignore)
          tx

      match updateResult with
      | Error ex -> return Error ex
      | Ok() ->
        MigrationLog.recordUpdate tx "student" [ "id", box item.Id; "name", box item.Name; "age", box item.Age ]
        return Ok()
    }

  static member Delete (id: int64) (tx: SqliteTransaction) : Task<Result<unit, SqliteException>> =
    task {
      let! deleteResult =
        executeWriteUnit
          "DELETE FROM student WHERE id = @id"
          (fun cmd -> cmd.Parameters.AddWithValue("@id", id) |> ignore)
          tx

      match deleteResult with
      | Error ex -> return Error ex
      | Ok() ->
        MigrationLog.recordDelete tx "student" [ "id", box id ]
        return Ok()
    }

  static member DeleteAll(tx: SqliteTransaction) : Task<Result<unit, SqliteException>> =
    executeWriteUnit "DELETE FROM student" (fun _ -> ()) tx

  static member SelectByName (name: string) (tx: SqliteTransaction) : Task<Result<Student list, SqliteException>> =
    queryList
      "SELECT id, name, age FROM student WHERE name = @name"
      (fun cmd -> cmd.Parameters.AddWithValue("@name", name) |> ignore)
      (fun reader ->
        { Id = reader.GetInt64 0
          Name = reader.GetString 1
          Age = reader.GetInt64 2 })
      tx
