namespace MigLib.Codegen

module internal Introspection =
  open System
  open Microsoft.Data.Sqlite
  open MigLib.Sqlite
  open MigLib.Codegen.Naming
  open MigLib.Codegen.Types

  let private affinity (declaredType: string) =
    let t =
      if isNull declaredType then
        ""
      else
        declaredType.Trim().ToUpperInvariant()

    if t.Contains "INT" then
      "INTEGER"
    elif t.Contains "CHAR" || t.Contains "CLOB" || t.Contains "TEXT" then
      "TEXT"
    elif t.Contains "BLOB" || t = "" then
      "BLOB"
    elif t.Contains "REAL" || t.Contains "FLOA" || t.Contains "DOUB" then
      "REAL"
    else
      "NUMERIC"

  let private readTableSql (conn: SqliteConnection) (name: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT sql FROM sqlite_master WHERE type IN ('table','view') AND name = $name"
    cmd.Parameters.AddWithValue("$name", name) |> ignore
    let value = cmd.ExecuteScalar()

    if isNull value || value = box DBNull.Value then
      None
    else
      Some(string value)

  let private listRelationNames (conn: SqliteConnection) =
    use cmd = conn.CreateCommand()

    cmd.CommandText <-
      """
      SELECT name, type
      FROM sqlite_master
      WHERE type IN ('table', 'view')
        AND name NOT LIKE 'sqlite_%'
      ORDER BY name
      """

    use reader = cmd.ExecuteReader()
    let acc = ResizeArray<string * RelationKind>()

    while reader.Read() do
      let name = reader.GetString 0

      let kind =
        if reader.GetString(1).Equals("view", StringComparison.OrdinalIgnoreCase) then
          RelationKind.View
        else
          RelationKind.Table

      acc.Add(name, kind)

    acc |> List.ofSeq

  let private loadColumns (conn: SqliteConnection) (name: string) (kind: RelationKind) (createSql: string option) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- $"PRAGMA table_info({quoteSqlIdent name})"
    use reader = cmd.ExecuteReader()
    let columns = ResizeArray<ColumnInfo>()

    let autoIncInSql =
      match createSql with
      | Some sql -> sql.IndexOf("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase) >= 0
      | None -> false

    while reader.Read() do
      let colName = reader.GetString 1
      let declaredType = if reader.IsDBNull 2 then "" else reader.GetString 2
      // SQLite PRAGMA table_info always reports notnull=0 for view columns, even when
      // every source column is NOT NULL. Treat views as non-null so generated row types
      // match apps that forbid SQL NULLs (callers can still use option return types for
      // "row missing", e.g. selectById / selectOneBy).
      let notNull =
        match kind with
        | RelationKind.View -> true
        | RelationKind.Table -> reader.GetInt64 3 <> 0L

      let pkOrdinal = int (reader.GetInt64 5)

      columns.Add
        { name = colName
          declaredType = declaredType
          notNull = notNull
          pkOrdinal = pkOrdinal
          isAutoIncrement = false // filled below
        }

    let cols = columns |> List.ofSeq

    let integerPkColumns =
      cols
      |> List.filter (fun c -> c.pkOrdinal > 0 && affinity c.declaredType = "INTEGER")

    // Only omit columns from insert inputs when the CREATE TABLE uses AUTOINCREMENT.
    // Sole INTEGER PRIMARY KEY without AUTOINCREMENT is still a rowid alias in SQLite,
    // but apps often supply application-assigned keys (e.g. telegram chat_id).
    let soleIntegerPk =
      match integerPkColumns with
      | [ c ] when cols |> List.filter (fun x -> x.pkOrdinal > 0) |> List.length = 1 -> Some c.name
      | _ -> None

    cols
    |> List.map (fun c ->
      let isAuto =
        match soleIntegerPk with
        | Some pkName when c.name = pkName -> autoIncInSql
        | _ -> false

      { c with isAutoIncrement = isAuto })

  let introspect (dbPath: string) : Result<Map<string, RelationInfo>, string> =
    try
      ensureInitialized ()

      use conn = openConnection dbPath
      let names = listRelationNames conn

      let map =
        names
        |> List.map (fun (name, kind) ->
          let sql = readTableSql conn name
          let columns = loadColumns conn name kind sql

          name,
          { name = name
            kind = kind
            columns = columns })
        |> Map.ofList

      Ok map
    with ex ->
      Error ex.Message
