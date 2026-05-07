module ExampleApp.Db

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Generated

let GeneratedSchema: ResolvedGeneratedSchemaModule =
  { schema =
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
              queryLikeAnnotations = [ { columns = [ "name" ] } ]
              queryByOrCreateAnnotations = [ { columns = [ "name" ] } ]
              selectOneAnnotations = [ SelectOneAnnotation ]
              insertOrIgnoreAnnotations = [ InsertOrIgnoreAnnotation ]
              deleteAllAnnotations = [ DeleteAllAnnotation ]
              upsertAnnotations = [ UpsertAnnotation ] } ]
        indexes = []
        triggers = [] }
    schemaHash = "e3985191e357ac09"
    dbApp = "ExampleApp"
    defaultDbInstance = "main" }

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
          Recording.recordInsert tx "student" [ "name", box item.Name; "age", box item.Age; "id", box newId ]
          return Ok newId
        })

  static member InsertOrIgnore (item: Student) (tx: SqliteTransaction) : Task<Result<int64 option, SqliteException>> =
    executeInsertOrIgnore
      "INSERT OR IGNORE INTO student (name, age) VALUES (@name, @age)"
      (fun cmd ->
        cmd.Parameters.AddWithValue("@name", item.Name) |> ignore
        cmd.Parameters.AddWithValue("@age", item.Age) |> ignore)
      tx
      (fun newId ->
        task {
          match newId with
          | None -> return Ok None
          | Some newId ->
            Recording.recordInsert tx "student" [ "name", box item.Name; "age", box item.Age; "id", box newId ]
            return Ok(Some newId)
        })

  static member Upsert (item: Student) (tx: SqliteTransaction) : Task<Result<unit, SqliteException>> =
    upsertByExisting (fun () -> Student.SelectById item.Id tx) (fun () -> Student.Update item tx) (fun () ->
      Student.Insert item tx)

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
        Recording.recordUpdate tx "student" [ "id", box item.Id; "name", box item.Name; "age", box item.Age ]
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
        Recording.recordDelete tx "student" [ "id", box id ]
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

  static member SelectNameLike (name: string) (tx: SqliteTransaction) : Task<Result<Student list, SqliteException>> =
    queryList
      "SELECT id, name, age FROM student WHERE name LIKE \'%\' || @name || \'%\'"
      (fun cmd -> cmd.Parameters.AddWithValue("@name", name) |> ignore)
      (fun reader ->
        { Id = reader.GetInt64 0
          Name = reader.GetString 1
          Age = reader.GetInt64 2 })
      tx

  static member SelectByNameOrInsert
    (newItem: Student)
    (tx: SqliteTransaction)
    : Task<Result<Student, SqliteException>> =
    let name = newItem.Name

    let select () =
      querySingle
        "SELECT id, name, age FROM student WHERE name = @name LIMIT 1"
        (fun cmd -> cmd.Parameters.AddWithValue("@name", name) |> ignore)
        (fun reader ->
          { Id = reader.GetInt64 0
            Name = reader.GetString 1
            Age = reader.GetInt64 2 })
        tx

    querySingleOrInsert select (fun () -> Student.Insert newItem tx)
