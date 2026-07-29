namespace MigLib

/// Shared ADO.NET helpers used by generated query modules and hand-written stores.
module Query =
  open System
  open System.Globalization
  open System.Threading.Tasks
  open Microsoft.Data.Sqlite
  open MigLib.Runtime.TxnStep

  let private boxValue (value: obj) =
    if isNull value then box DBNull.Value else value

  let addParam (cmd: SqliteCommand) (name: string) (value: obj) =
    let p = cmd.CreateParameter()
    p.ParameterName <- name
    p.Value <- boxValue value
    cmd.Parameters.Add p |> ignore

  let private withCommand (sql: string) (parameters: (string * obj) list) (tx: SqliteTransaction) (body: SqliteCommand -> 'a) =
    use cmd = new SqliteCommand(sql, tx.Connection, tx)

    for name, value in parameters do
      addParam cmd name value

    body cmd

  let trySqlite (f: unit -> 'a) : Result<'a, SqliteException> =
    try
      Ok(f ())
    with :? SqliteException as ex ->
      Error ex

  let exec (sql: string) (parameters: (string * obj) list) : TxnStep<int> =
    fun tx ->
      task {
        return
          trySqlite (fun () ->
            withCommand sql parameters tx (fun cmd -> cmd.ExecuteNonQuery()))
      }

  let scalar (sql: string) (parameters: (string * obj) list) : TxnStep<obj> =
    fun tx ->
      task {
        return
          trySqlite (fun () ->
            withCommand sql parameters tx (fun cmd -> cmd.ExecuteScalar()))
      }

  let queryList (sql: string) (parameters: (string * obj) list) (map: SqliteDataReader -> 'a) : TxnStep<'a list> =
    fun tx ->
      task {
        return
          trySqlite (fun () ->
            withCommand sql parameters tx (fun cmd ->
              use reader = cmd.ExecuteReader()
              let acc = ResizeArray<'a>()

              while reader.Read() do
                acc.Add(map reader)

              acc |> List.ofSeq))
      }

  let queryOne (sql: string) (parameters: (string * obj) list) (map: SqliteDataReader -> 'a) : TxnStep<'a option> =
    bind (queryList sql parameters map) (fun rows -> result (List.tryHead rows))

  let lastInsertRowId : TxnStep<int64> =
    fun tx ->
      task {
        return
          trySqlite (fun () ->
            use cmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
            let value = cmd.ExecuteScalar()

            match value with
            | :? int64 as id -> id
            | :? int32 as id -> int64 id
            | _ -> Convert.ToInt64(value, CultureInfo.InvariantCulture))
      }

  // --- Reader helpers (generated map functions call these) ---

  let isDbNull (r: SqliteDataReader) (ordinal: int) = r.IsDBNull ordinal

  let readInt64 (r: SqliteDataReader) (ordinal: int) = r.GetInt64 ordinal

  let readInt64Option (r: SqliteDataReader) (ordinal: int) =
    if isDbNull r ordinal then None else Some(readInt64 r ordinal)

  let readInt (r: SqliteDataReader) (ordinal: int) = r.GetInt32 ordinal

  let readIntOption (r: SqliteDataReader) (ordinal: int) =
    if isDbNull r ordinal then None else Some(readInt r ordinal)

  let readUInt (r: SqliteDataReader) (ordinal: int) = uint32 (r.GetInt64 ordinal)

  let readUIntOption (r: SqliteDataReader) (ordinal: int) =
    if isDbNull r ordinal then None else Some(readUInt r ordinal)

  let readFloat (r: SqliteDataReader) (ordinal: int) = r.GetDouble ordinal

  let readFloatOption (r: SqliteDataReader) (ordinal: int) =
    if isDbNull r ordinal then None else Some(readFloat r ordinal)

  let readString (r: SqliteDataReader) (ordinal: int) = r.GetString ordinal

  let readStringOption (r: SqliteDataReader) (ordinal: int) =
    if isDbNull r ordinal then None else Some(readString r ordinal)

  let readBlob (r: SqliteDataReader) (ordinal: int) =
    let len = r.GetBytes(ordinal, 0L, null, 0, 0) |> int
    let buffer = Array.zeroCreate<byte> len
    r.GetBytes(ordinal, 0L, buffer, 0, len) |> ignore
    buffer

  let readBlobOption (r: SqliteDataReader) (ordinal: int) =
    if isDbNull r ordinal then None else Some(readBlob r ordinal)

  let readBool (r: SqliteDataReader) (ordinal: int) = r.GetInt64 ordinal <> 0L

  let readBoolOption (r: SqliteDataReader) (ordinal: int) =
    if isDbNull r ordinal then None else Some(readBool r ordinal)

  let private parseRfc3339 (text: string) =
    DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)

  let readDateTimeOffset (r: SqliteDataReader) (ordinal: int) =
    parseRfc3339 (r.GetString ordinal)

  let readDateTimeOffsetOption (r: SqliteDataReader) (ordinal: int) =
    if isDbNull r ordinal then
      None
    else
      Some(readDateTimeOffset r ordinal)

  // --- Parameter boxing helpers ---

  let boxBool (value: bool) = box (if value then 1L else 0L)
  let boxBoolOption (value: bool option) = match value with Some v -> boxBool v | None -> null
  let boxDateTimeOffset (value: DateTimeOffset) = box (value.ToString("o", CultureInfo.InvariantCulture))

  let boxDateTimeOffsetOption (value: DateTimeOffset option) =
    match value with
    | Some v -> boxDateTimeOffset v
    | None -> null

  let boxOption (value: 'a option) =
    match value with
    | Some v -> box v
    | None -> null

