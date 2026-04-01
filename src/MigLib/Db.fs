module MigLib.Db

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Util

[<Literal>]
let Rfc3339UtcNow = "strftime('%Y-%m-%dT%H:%M:%SZ', 'now', 'utc')"

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

[<AttributeUsage(AttributeTargets.Class)>]
type UpsertAttribute() =
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

[<AttributeUsage(AttributeTargets.Class ||| AttributeTargets.Property, AllowMultiple = false)>]
type PreviousNameAttribute(name: string) =
  inherit Attribute()
  member _.Name = name

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
type DropColumnAttribute(name: string) =
  inherit Attribute()
  member _.Name = name

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
let private sqliteInitialized = lazy (SQLitePCL.Batteries_V2.Init())

let private sqliteConnectionString (dbPath: string) = $"Data Source={dbPath}"

let private ensureSqliteInitialized () = sqliteInitialized.Force()

let openSqliteConnection (dbPath: string) =
  ensureSqliteInitialized ()
  let connection = new SqliteConnection(sqliteConnectionString dbPath)
  connection.Open()
  connection

type StartupDatabaseState =
  | Missing
  | Ready
  | Migrating
  | Invalid of reason: string

type StartupDatabaseDecision =
  | UseExisting of dbPath: string
  | WaitForMigration of dbPath: string
  | MigrateThisInstance of dbPath: string
  | ExitEarly of dbPath: string * reason: string

let private resolveDatabasePath (configuredPath: string) : Result<string, string> =
  if String.IsNullOrWhiteSpace configuredPath then
    Error "Configured database path is empty."
  else
    Ok(Path.GetFullPath configuredPath)

let resolveDatabaseFilePath (configuredDirectory: string) (dbFileName: string) : Result<string, string> =
  match resolveDatabasePath configuredDirectory with
  | Error message -> Error message
  | Ok resolvedDirectory ->
    if String.IsNullOrWhiteSpace dbFileName then
      Error "Configured database file name is empty."
    elif Path.IsPathRooted dbFileName || dbFileName <> Path.GetFileName dbFileName then
      Error $"Configured database file name must be a file name only, not a path: {dbFileName}"
    else
      Ok(Path.Combine(resolvedDirectory, dbFileName))

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

let private tableExistsInConnection (connection: SqliteConnection) (tableName: string) : Task<bool> =
  task {
    use cmd =
      new SqliteCommand("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1", connection)

    cmd.Parameters.AddWithValue("@name", tableName) |> ignore
    let! scalar = cmd.ExecuteScalarAsync()
    return not (isNull scalar)
  }

let private tryReadStatusValueFromConnection
  (connection: SqliteConnection)
  (tableName: string)
  : Task<Result<string option, SqliteException>> =
  task {
    try
      let! statusTableExists = tableExistsInConnection connection tableName

      if not statusTableExists then
        return Ok None
      else
        use cmd =
          new SqliteCommand($"SELECT status FROM {tableName} WHERE id = 0 LIMIT 1", connection)

        let! statusObj = cmd.ExecuteScalarAsync()

        if isNull statusObj then
          return Ok None
        else
          return Ok(Some(string statusObj))
    with :? SqliteException as ex ->
      return Error ex
  }

let private getMatchingContext (tx: SqliteTransaction) : TxnContext option =
  match txnContext.Value with
  | Some context when Object.ReferenceEquals(context.tx, tx) -> Some context
  | _ -> None

let getStartupDatabaseState (dbPath: string) : Task<Result<StartupDatabaseState, SqliteException>> =
  task {
    match resolveDatabasePath dbPath with
    | Error message -> return Error(SqliteException(message, 0))
    | Ok resolvedDbPath ->
      if not (File.Exists resolvedDbPath) then
        return Ok Missing
      else
        try
          use connection = openSqliteConnection resolvedDbPath
          let! statusResult = tryReadStatusValueFromConnection connection "_migration_status"

          match statusResult with
          | Error ex -> return Error ex
          | Ok None ->
            let! hasStatusTable = tableExistsInConnection connection "_migration_status"

            if hasStatusTable then
              return
                Ok(
                  Invalid
                    "Target database has a _migration_status table but no status row at id = 0. Run migration again or repair the target before serving traffic."
                )
            else
              return Ok Ready
          | Ok(Some status) when status.Equals("ready", StringComparison.OrdinalIgnoreCase) -> return Ok Ready
          | Ok(Some status) when status.Equals("migrating", StringComparison.OrdinalIgnoreCase) -> return Ok Migrating
          | Ok(Some status) ->
            return
              Ok(
                Invalid
                  $"Unsupported _migration_status value '{status}'. Expected 'migrating' or 'ready' before serving traffic."
              )
        with :? SqliteException as ex ->
          return Error ex
  }

let getStartupDatabaseDecision
  (configuredDirectory: string)
  (dbFileName: string)
  : Task<Result<StartupDatabaseDecision, SqliteException>> =
  taskResult {
    let! dbPath =
      (resolveDatabaseFilePath configuredDirectory dbFileName
       |> TaskResultEx.ofResultMapError (fun message -> SqliteException(message, 0))
      : Task<Result<string, SqliteException>>)

    let! state = (getStartupDatabaseState dbPath: Task<Result<StartupDatabaseState, SqliteException>>)

    return
      match state with
      | Missing -> MigrateThisInstance dbPath
      | Ready -> UseExisting dbPath
      | Migrating -> WaitForMigration dbPath
      | Invalid reason -> ExitEarly(dbPath, reason)
  }

let waitForStartupDatabaseReady
  (dbPath: string)
  (pollInterval: TimeSpan)
  (cancellationToken: CancellationToken)
  : Task<Result<unit, SqliteException>> =
  task {
    try
      let interval =
        if pollInterval <= TimeSpan.Zero then
          TimeSpan.FromMilliseconds 100.0
        else
          pollInterval

      let mutable keepWaiting = true
      let mutable result = Ok()

      while keepWaiting do
        let! stateResult = getStartupDatabaseState dbPath

        match stateResult with
        | Error ex ->
          result <- Error ex
          keepWaiting <- false
        | Ok Ready ->
          result <- Ok()
          keepWaiting <- false
        | Ok Migrating -> do! Task.Delay(interval, cancellationToken)
        | Ok Missing ->
          result <- Error(SqliteException($"Target database was not found while waiting for readiness: {dbPath}", 0))

          keepWaiting <- false
        | Ok(Invalid reason) ->
          result <- Error(SqliteException(reason, 0))
          keepWaiting <- false

      return result
    with :? OperationCanceledException ->
      return Error(SqliteException("Waiting for startup database readiness was canceled.", 0))
  }

let private tableExists (tx: SqliteTransaction) (tableName: string) : Task<bool> =
  task {
    use cmd =
      new SqliteCommand("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1", tx.Connection, tx)

    cmd.Parameters.AddWithValue("@name", tableName) |> ignore
    let! scalar = cmd.ExecuteScalarAsync()
    return not (isNull scalar)
  }

let private tryReadStatusValue
  (tx: SqliteTransaction)
  (tableName: string)
  : Task<Result<string option, SqliteException>> =
  task {
    try
      let! statusTableExists = tableExists tx tableName

      if not statusTableExists then
        return Ok None
      else
        use cmd =
          new SqliteCommand($"SELECT status FROM {tableName} WHERE id = 0 LIMIT 1", tx.Connection, tx)

        let! statusObj = cmd.ExecuteScalarAsync()

        if isNull statusObj then
          return Ok None
        else
          return Ok(Some(string statusObj))
    with :? SqliteException as ex ->
      return Error ex
  }

let private ensureNewDatabaseReadyForTransactions (tx: SqliteTransaction) : Task<Result<unit, SqliteException>> =
  task {
    let! statusResult = tryReadStatusValue tx "_migration_status"

    match statusResult with
    | Error ex -> return Error ex
    | Ok None ->
      let! hasStatusTable = tableExists tx "_migration_status"

      if hasStatusTable then
        return
          Error(
            SqliteException(
              "Target database has a _migration_status table but no status row at id = 0. Run `mig migrate` again or repair the target before serving traffic.",
              0
            )
          )
      else
        return Ok()
    | Ok(Some status) when status.Equals("ready", StringComparison.OrdinalIgnoreCase) -> return Ok()
    | Ok(Some status) when status.Equals("migrating", StringComparison.OrdinalIgnoreCase) ->
      return Error(SqliteException("Target database is still migrating. Run `mig cutover` before serving requests.", 0))
    | Ok(Some status) ->
      return
        Error(
          SqliteException(
            $"Unsupported _migration_status value '{status}'. Expected 'migrating' or 'ready' before serving requests.",
            0
          )
        )
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

type TxnStep<'a> = SqliteTransaction -> Task<Result<'a, SqliteException>>

module private TxnStep =
  let private toSqliteException (error: 'e) : SqliteException =
    match box error with
    | null -> SqliteException("Task<Result<_, _>> returned null error.", 0)
    | :? SqliteException as sqliteError -> sqliteError
    | :? exn as exceptionError -> SqliteException(exceptionError.Message, 0)
    | _ -> SqliteException(string error, 0)

  let zero () : TxnStep<unit> = fun _ -> Task.FromResult(Ok())

  let result (x: 'a) : TxnStep<'a> = fun _ -> Task.FromResult(Ok x)

  let returnFrom (m: TxnStep<'a>) : TxnStep<'a> = m

  let bind (m: TxnStep<'a>) (f: 'a -> TxnStep<'b>) : TxnStep<'b> =
    fun txn ->
      task {
        let! result = m txn

        match result with
        | Ok value -> return! f value txn
        | Error ex -> return Error ex
      }

  let bindTask (m: Task<'a>) (f: 'a -> TxnStep<'b>) : TxnStep<'b> =
    fun txn ->
      task {
        let! value = m
        return! f value txn
      }

  let bindTaskResult (m: Task<Result<'a, 'e>>) (f: 'a -> TxnStep<'b>) : TxnStep<'b> =
    fun txn ->
      task {
        let! result = m

        match result with
        | Ok value -> return! f value txn
        | Error error -> return Error(toSqliteException error)
      }

  let combine (m: TxnStep<unit>) (f: TxnStep<'a>) : TxnStep<'a> = bind m (fun () -> f)

  let delay (f: unit -> TxnStep<'a>) : TxnStep<'a> = fun txn -> f () txn

  let forEach (items: 'a seq) (body: 'a -> TxnStep<unit>) : TxnStep<unit> =
    fun txn ->
      task {
        let mutable error = None

        use enumerator = items.GetEnumerator()

        while error.IsNone && enumerator.MoveNext() do
          let! result = body enumerator.Current txn

          match result with
          | Ok() -> ()
          | Error ex -> error <- Some ex

        match error with
        | Some ex -> return Error ex
        | None -> return Ok()
      }

let internal runTransactionInternal
  (dbPath: string)
  (mapDbError: SqliteException -> 'e)
  (body: SqliteTransaction -> Task<Result<'a, 'e>>)
  : Task<Result<'a, 'e>> =
  task {
    match resolveDatabasePath dbPath with
    | Error message -> return Error(mapDbError (SqliteException(message, 0)))
    | Ok resolvedDbPath ->
      use connection = openSqliteConnection resolvedDbPath
      use transaction = connection.BeginTransaction()
      let! readinessResult = ensureNewDatabaseReadyForTransactions transaction

      match readinessResult with
      | Error ex ->
        transaction.Rollback()
        return Error(mapDbError ex)
      | Ok() ->
        let! mode = detectMigrationMode transaction

        let context =
          { tx = transaction
            mode = mode
            writes = ResizeArray() }

        let previousContext = txnContext.Value
        txnContext.Value <- Some context

        try
          try
            let! result = body transaction

            match result with
            | Ok value ->
              let! flushResult = flushRecordedWrites context

              match flushResult with
              | Ok() ->
                transaction.Commit()
                return Ok value
              | Error ex ->
                transaction.Rollback()
                return Error(mapDbError ex)
            | Error _ ->
              transaction.Rollback()
              return result
          with :? SqliteException as ex ->
            transaction.Rollback()
            return Error(mapDbError ex)
        finally
          txnContext.Value <- previousContext
  }

type DbRuntime(dbPath: string) =
  member _.DbPath = dbPath

  member _.RunInTransaction
    (mapDbError: SqliteException -> 'e)
    (body: SqliteTransaction -> Task<Result<'a, 'e>>)
    : Task<Result<'a, 'e>> =
    runTransactionInternal dbPath mapDbError body

type IHasDbRuntime =
  abstract DbRuntime: DbRuntime

// DbTxnBuilder computation expression
type DbTxnBuilder(dbPath: string) =
  member _.DbPath = dbPath
  member _.DbRuntime = DbRuntime dbPath

  member _.Run(f: TxnStep<'a>) : Task<Result<'a, SqliteException>> = runTransactionInternal dbPath id f

  member _.Zero() : TxnStep<unit> = TxnStep.zero ()

  member _.Return(x: 'a) : TxnStep<'a> = TxnStep.result x

  member _.ReturnFrom(m: TxnStep<'a>) : TxnStep<'a> = TxnStep.returnFrom m

  member _.Bind(m: TxnStep<'a>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = TxnStep.bind m f

  member _.Bind(m: Task<'a>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = TxnStep.bindTask m f

  member _.Bind(m: Task<Result<'a, 'e>>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = TxnStep.bindTaskResult m f

  member _.Combine(m: TxnStep<unit>, f: TxnStep<'a>) : TxnStep<'a> = TxnStep.combine m f

  member _.Delay(f: unit -> TxnStep<'a>) : TxnStep<'a> = TxnStep.delay f

  member _.For(items: 'a seq, body: 'a -> TxnStep<unit>) : TxnStep<unit> = TxnStep.forEach items body

type TxnBuilder() =
  member _.Run(f: TxnStep<'a>) : TxnStep<'a> = f

  member _.Zero() : TxnStep<unit> = TxnStep.zero ()

  member _.Return(x: 'a) : TxnStep<'a> = TxnStep.result x

  member _.ReturnFrom(m: TxnStep<'a>) : TxnStep<'a> = TxnStep.returnFrom m

  member _.Bind(m: TxnStep<'a>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = TxnStep.bind m f

  member _.Bind(m: Task<'a>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = TxnStep.bindTask m f

  member _.Bind(m: Task<Result<'a, 'e>>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = TxnStep.bindTaskResult m f

  member _.Combine(m: TxnStep<unit>, f: TxnStep<'a>) : TxnStep<'a> = TxnStep.combine m f

  member _.Delay(f: unit -> TxnStep<'a>) : TxnStep<'a> = TxnStep.delay f

  member _.For(items: 'a seq, body: 'a -> TxnStep<unit>) : TxnStep<unit> = TxnStep.forEach items body

let dbTxn dbPath = DbTxnBuilder dbPath
let dbRuntime dbPath = DbRuntime dbPath
let txn = TxnBuilder()
