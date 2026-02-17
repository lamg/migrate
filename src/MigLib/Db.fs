module MigLib.Db

open System
open System.Collections.Generic
open System.Globalization
open System.Text.Json.Nodes
open System.Threading
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

type private MigrationMode =
  | Normal
  | Recording
  | Draining

type private MigrationWrite =
  { operation: string
    tableName: string
    rowDataJson: string }

type private TxnContext =
  { tx: SqliteTransaction
    mode: MigrationMode
    writes: ResizeArray<MigrationWrite> }

let private txnContext = AsyncLocal<TxnContext option>()

let private toJsonNode (value: obj) : JsonNode =
  if isNull value || Object.ReferenceEquals(value, DBNull.Value) then
    null
  else
    match value with
    | :? string as v -> JsonValue.Create v :> JsonNode
    | :? int8 as v -> JsonValue.Create v :> JsonNode
    | :? int16 as v -> JsonValue.Create v :> JsonNode
    | :? int as v -> JsonValue.Create v :> JsonNode
    | :? int64 as v -> JsonValue.Create v :> JsonNode
    | :? uint8 as v -> JsonValue.Create v :> JsonNode
    | :? uint16 as v -> JsonValue.Create v :> JsonNode
    | :? uint32 as v -> JsonValue.Create v :> JsonNode
    | :? uint64 as v -> JsonValue.Create v :> JsonNode
    | :? float32 as v -> JsonValue.Create v :> JsonNode
    | :? float as v -> JsonValue.Create v :> JsonNode
    | :? decimal as v -> JsonValue.Create v :> JsonNode
    | :? bool as v -> JsonValue.Create v :> JsonNode
    | :? DateTime as v -> JsonValue.Create(v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) :> JsonNode
    | :? (byte[]) as v -> JsonValue.Create(Convert.ToBase64String v) :> JsonNode
    | _ -> JsonValue.Create(value.ToString()) :> JsonNode

let private serializeRowData (rowData: (string * obj) list) : string =
  let json = JsonObject()

  for name, value in rowData do
    json[name] <- toJsonNode value

  json.ToJsonString()

let private getMatchingContext (tx: SqliteTransaction) : TxnContext option =
  match txnContext.Value with
  | Some context when Object.ReferenceEquals(context.tx, tx) -> Some context
  | _ -> None

let private tableExists (tx: SqliteTransaction) (tableName: string) : Task<bool> =
  task {
    use cmd =
      new SqliteCommand("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1", tx.Connection, tx)

    cmd.Parameters.AddWithValue("@name", tableName) |> ignore
    let! scalar = cmd.ExecuteScalarAsync()
    return not (isNull scalar)
  }

let private detectMigrationMode (tx: SqliteTransaction) : Task<MigrationMode> =
  task {
    let! markerExists = tableExists tx "_migration_marker"

    if not markerExists then
      return Normal
    else
      use cmd =
        new SqliteCommand("SELECT status FROM _migration_marker WHERE id = 0 LIMIT 1", tx.Connection, tx)

      let! statusObj = cmd.ExecuteScalarAsync()

      if isNull statusObj then
        return Normal
      else
        match string statusObj with
        | "recording" -> return Recording
        | "draining" -> return Draining
        | _ -> return Normal
  }

let private flushRecordedWrites (context: TxnContext) : Task<Result<unit, SqliteException>> =
  task {
    try
      match context.mode with
      | Recording when context.writes.Count > 0 ->
        let! logExists = tableExists context.tx "_migration_log"

        if not logExists then
          return Error(SqliteException("Migration marker is set to recording, but _migration_log table is missing.", 0))
        else
          use txnIdCmd =
            new SqliteCommand(
              "SELECT COALESCE(MAX(txn_id), 0) + 1 FROM _migration_log",
              context.tx.Connection,
              context.tx
            )

          let! txnIdObj = txnIdCmd.ExecuteScalarAsync()
          let txnId = Convert.ToInt64(txnIdObj, CultureInfo.InvariantCulture)

          for index in 0 .. context.writes.Count - 1 do
            let entry = context.writes[index]

            use insertCmd =
              new SqliteCommand(
                "INSERT INTO _migration_log (txn_id, ordering, operation, table_name, row_data) VALUES (@txn_id, @ordering, @operation, @table_name, @row_data)",
                context.tx.Connection,
                context.tx
              )

            insertCmd.Parameters.AddWithValue("@txn_id", txnId) |> ignore
            insertCmd.Parameters.AddWithValue("@ordering", index + 1) |> ignore
            insertCmd.Parameters.AddWithValue("@operation", entry.operation) |> ignore
            insertCmd.Parameters.AddWithValue("@table_name", entry.tableName) |> ignore
            insertCmd.Parameters.AddWithValue("@row_data", entry.rowDataJson) |> ignore

            let! _ = insertCmd.ExecuteNonQueryAsync()
            ()

          return Ok()
      | _ -> return Ok()
    with :? SqliteException as ex ->
      return Error ex
  }

module MigrationLog =
  let ensureWriteAllowed (tx: SqliteTransaction) : unit =
    match getMatchingContext tx with
    | Some context when context.mode = Draining ->
      raise (SqliteException("Writes are unavailable while migration drain is in progress.", 0))
    | _ -> ()

  let private recordWrite
    (tx: SqliteTransaction)
    (operation: string)
    (tableName: string)
    (rowData: (string * obj) list)
    =
    ensureWriteAllowed tx

    match getMatchingContext tx with
    | Some context when context.mode = Recording ->
      context.writes.Add
        { operation = operation
          tableName = tableName
          rowDataJson = serializeRowData rowData }
    | _ -> ()

  let recordInsert (tx: SqliteTransaction) (tableName: string) (rowData: (string * obj) list) : unit =
    recordWrite tx "insert" tableName rowData

  let recordUpdate (tx: SqliteTransaction) (tableName: string) (rowData: (string * obj) list) : unit =
    recordWrite tx "update" tableName rowData

  let recordDelete (tx: SqliteTransaction) (tableName: string) (rowData: (string * obj) list) : unit =
    recordWrite tx "delete" tableName rowData

// TaskTxnBuilder computation expression
type TaskTxnBuilder(dbPath: string) =
  member _.DbPath = dbPath

  member _.Run(f: SqliteTransaction -> Task<Result<'a, SqliteException>>) : Task<Result<'a, SqliteException>> =
    task {
      use connection = new SqliteConnection $"Data Source={dbPath}"
      do! connection.OpenAsync()
      use transaction = connection.BeginTransaction()
      let! mode = detectMigrationMode transaction

      let context =
        { tx = transaction
          mode = mode
          writes = ResizeArray() }

      let previousContext = txnContext.Value
      txnContext.Value <- Some context

      try
        try
          let! result = f transaction

          match result with
          | Ok value ->
            let! flushResult = flushRecordedWrites context

            match flushResult with
            | Ok() ->
              transaction.Commit()
              return Ok value
            | Error ex ->
              transaction.Rollback()
              return Error ex
          | Error _ ->
            transaction.Rollback()
            return result
        with :? SqliteException as ex ->
          transaction.Rollback()
          return Error ex
      finally
        txnContext.Value <- previousContext
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
