module MigLib.Db.Core

open System
open System.Globalization
open System.IO
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite

type MigrationMode =
  | Normal
  | Recording
  | Draining

type MigrationWrite =
  { operation: string
    tableName: string
    rowDataJson: string }

type TxnContext =
  { tx: SqliteTransaction
    mode: MigrationMode
    writes: ResizeArray<MigrationWrite> }

let txnContext = AsyncLocal<TxnContext option>()

let resolveDatabasePath (configuredPath: string) : Result<string, string> =
  if String.IsNullOrWhiteSpace configuredPath then
    Error "Configured database path is empty."
  else
    Ok(Path.GetFullPath configuredPath)

let openSqliteConnection (dbPath: string) = MigLib.Sqlite.openConnection dbPath

let serializeRowData (rowData: (string * obj) list) : string =
  let toJsonNode (value: obj) : JsonNode =
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
      | :? DateTime as v ->
        JsonValue.Create(v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) :> JsonNode
      | :? (byte[]) as v -> JsonValue.Create(Convert.ToBase64String v) :> JsonNode
      | _ -> JsonValue.Create(value.ToString()) :> JsonNode

  let json = JsonObject()

  for name, value in rowData do
    json[name] <- toJsonNode value

  json.ToJsonString()

let tableExists (tx: SqliteTransaction) (tableName: string) : Task<bool> =
  task {
    use cmd =
      new SqliteCommand("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1", tx.Connection, tx)

    cmd.Parameters.AddWithValue("@name", tableName) |> ignore
    let! scalar = cmd.ExecuteScalarAsync()
    return not (isNull scalar)
  }

let tryReadStatusValue (tx: SqliteTransaction) (tableName: string) : Task<Result<string option, SqliteException>> =
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

let getMatchingContext (tx: SqliteTransaction) : TxnContext option =
  match txnContext.Value with
  | Some context when Object.ReferenceEquals(context.tx, tx) -> Some context
  | _ -> None
