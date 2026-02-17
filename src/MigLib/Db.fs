module MigLib.Db

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite

// Primary key attributes
[<AttributeUsage(AttributeTargets.Class)>]
type AutoIncPKAttribute(column: string) =
  inherit Attribute()
  member _.Column = column

[<AttributeUsage(AttributeTargets.Class)>]
type PKAttribute(column: string) =
  inherit Attribute()
  member _.Column = column

// Constraint attributes
[<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
type UniqueAttribute([<ParamArray>] columns: string array) =
  inherit Attribute()
  member _.Columns = columns

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
type DefaultAttribute(column: string, value: string) =
  inherit Attribute()
  member _.Column = column
  member _.Value = value

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
type DefaultExprAttribute(column: string, expr: string) =
  inherit Attribute()
  member _.Column = column
  member _.Expr = expr

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
type IndexAttribute([<ParamArray>] columns: string array) =
  inherit Attribute()
  member _.Columns = columns

// Query attributes
[<AttributeUsage(AttributeTargets.Class)>]
type SelectAllAttribute() =
  inherit Attribute()
  member val OrderBy: string = null with get, set

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
type SelectByAttribute([<ParamArray>] columns: string array) =
  inherit Attribute()
  member _.Columns = columns
  member val OrderBy: string = null with get, set

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
type SelectOneByAttribute([<ParamArray>] columns: string array) =
  inherit Attribute()
  member _.Columns = columns
  member val OrderBy: string = null with get, set

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
type SelectLikeAttribute(column: string) =
  inherit Attribute()
  member _.Column = column

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
type SelectByOrInsertAttribute([<ParamArray>] columns: string array) =
  inherit Attribute()
  member _.Columns = columns

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
type UpdateByAttribute(column: string) =
  inherit Attribute()
  member _.Column = column

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
type DeleteByAttribute(column: string) =
  inherit Attribute()
  member _.Column = column

[<AttributeUsage(AttributeTargets.Class)>]
type InsertOrIgnoreAttribute() =
  inherit Attribute()

// Foreign key action attributes
[<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
type OnDeleteCascadeAttribute(column: string) =
  inherit Attribute()
  member _.Column = column

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
type OnDeleteSetNullAttribute(column: string) =
  inherit Attribute()
  member _.Column = column

// View attributes
[<AttributeUsage(AttributeTargets.Class)>]
type ViewAttribute() =
  inherit Attribute()

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
type JoinAttribute(left: Type, right: Type) =
  inherit Attribute()
  member _.Left = left
  member _.Right = right

[<AttributeUsage(AttributeTargets.Class)>]
type ViewSqlAttribute(sql: string) =
  inherit Attribute()
  member _.Sql = sql

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
type OrderByAttribute(columns: string) =
  inherit Attribute()
  member _.Columns = columns

// TaskTxnBuilder computation expression
type TaskTxnBuilder(dbPath: string) =
  member _.DbPath = dbPath

  member _.Run(f: SqliteTransaction -> Task<Result<'a, SqliteException>>) : Task<Result<'a, SqliteException>> =
    task {
      use connection = new SqliteConnection $"Data Source={dbPath}"
      do! connection.OpenAsync()
      use transaction = connection.BeginTransaction()

      try
        let! result = f transaction

        match result with
        | Ok _ -> transaction.Commit()
        | Error _ -> transaction.Rollback()

        return result
      with :? SqliteException as ex ->
        transaction.Rollback()
        return Error ex
    }

  member _.Zero() : SqliteTransaction -> Task<Result<unit, SqliteException>> = fun _ -> Task.FromResult(Ok())

  member _.Return(x: 'a) : SqliteTransaction -> Task<Result<'a, SqliteException>> = fun _ -> Task.FromResult(Ok x)

  member _.Bind
    (
      m: SqliteTransaction -> Task<Result<'a, SqliteException>>,
      f: 'a -> SqliteTransaction -> Task<Result<'b, SqliteException>>
    ) : SqliteTransaction -> Task<Result<'b, SqliteException>> =
    fun txn ->
      task {
        let! result = m txn

        match result with
        | Ok a -> return! f a txn
        | Error e -> return Error e
      }

  member this.Combine
    (
      m: SqliteTransaction -> Task<Result<unit, SqliteException>>,
      f: SqliteTransaction -> Task<Result<'a, SqliteException>>
    ) : SqliteTransaction -> Task<Result<'a, SqliteException>> =
    this.Bind(m, fun () -> f)

  member _.Delay(f: unit -> SqliteTransaction -> Task<Result<'a, SqliteException>>) = fun txn -> f () txn

  member _.For
    (items: 'a seq, body: 'a -> SqliteTransaction -> Task<Result<unit, SqliteException>>)
    : SqliteTransaction -> Task<Result<unit, SqliteException>> =
    fun txn ->
      task {
        let mutable error = None

        use enumerator = items.GetEnumerator()

        while error.IsNone && enumerator.MoveNext() do
          let! result = body enumerator.Current txn

          match result with
          | Ok() -> ()
          | Error e -> error <- Some e

        match error with
        | Some e -> return Error e
        | None -> return Ok()
      }

let taskTxn dbPath = TaskTxnBuilder dbPath
