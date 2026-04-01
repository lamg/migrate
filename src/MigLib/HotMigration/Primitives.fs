namespace Mig

open System
open System.Globalization
open Microsoft.Data.Sqlite
open DeclarativeMigrations.Types

module internal HotMigrationPrimitives =
  let toSqliteError (message: string) = SqliteException(message, 0)

  let createCommand
    (connection: SqliteConnection)
    (transaction: SqliteTransaction option)
    (sql: string)
    : SqliteCommand =
    match transaction with
    | Some tx -> new SqliteCommand(sql, connection, tx)
    | None -> new SqliteCommand(sql, connection)

  let quoteIdentifier (name: string) =
    let escaped = name.Replace("\"", "\"\"")
    $"\"{escaped}\""

  let exprToInt64 (expr: Expr) : int64 option =
    match expr with
    | Integer value -> Some(int64 value)
    | Value value ->
      match Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
      | true, int64Value -> Some int64Value
      | _ -> None
    | _ -> None

  let exprToDbValue (expr: Expr) : obj =
    match expr with
    | String value -> box value
    | Integer value -> box value
    | Real value -> box value
    | Value value when value.Equals("NULL", StringComparison.OrdinalIgnoreCase) -> box DBNull.Value
    | Value value ->
      match Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
      | true, int64Value -> box int64Value
      | _ ->
        match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, doubleValue -> box doubleValue
        | _ -> box value

  let dbValueToExpr (value: obj) : Expr =
    if isNull value || Object.ReferenceEquals(value, DBNull.Value) then
      Value "NULL"
    else
      match value with
      | :? string as text -> String text
      | :? int8 as number -> Integer(int number)
      | :? int16 as number -> Integer(int number)
      | :? int as number -> Integer number
      | :? int64 as number ->
        if number >= int64 Int32.MinValue && number <= int64 Int32.MaxValue then
          Integer(int number)
        else
          Value(number.ToString CultureInfo.InvariantCulture)
      | :? uint8 as number -> Integer(int number)
      | :? uint16 as number ->
        if number <= uint16 Int32.MaxValue then
          Integer(int number)
        else
          Value(number.ToString CultureInfo.InvariantCulture)
      | :? uint32 as number ->
        if number <= uint32 Int32.MaxValue then
          Integer(int number)
        else
          Value(number.ToString CultureInfo.InvariantCulture)
      | :? uint64 as number ->
        if number <= uint64 Int32.MaxValue then
          Integer(int number)
        else
          Value(number.ToString CultureInfo.InvariantCulture)
      | :? float32 as number -> Real(float number)
      | :? float as number -> Real number
      | :? decimal as number -> Real(float number)
      | :? bool as flag -> Integer(if flag then 1 else 0)
      | :? DateTime as dt -> String(dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
      | :? (byte[]) as bytes -> Value(Convert.ToBase64String bytes)
      | _ -> Value(value.ToString())

  let parseSqlType (declaredType: string) : SqlType =
    let upper =
      if String.IsNullOrWhiteSpace declaredType then
        ""
      else
        declaredType.ToUpperInvariant()

    if upper.Contains "INT" then
      SqlInteger
    elif upper.Contains "REAL" || upper.Contains "FLOA" || upper.Contains "DOUB" then
      SqlReal
    elif upper.Contains "TIMESTAMP" || upper.Contains "DATE" || upper.Contains "TIME" then
      SqlTimestamp
    elif upper.Contains "CHAR" || upper.Contains "CLOB" || upper.Contains "TEXT" then
      SqlText
    elif upper.Contains "BLOB" then
      SqlFlexible
    else
      SqlFlexible

  let sqlTypeToSql (sqlType: SqlType) : string =
    match sqlType with
    | SqlInteger -> "INTEGER"
    | SqlText -> "TEXT"
    | SqlReal -> "REAL"
    | SqlTimestamp -> "TEXT"
    | SqlString -> "TEXT"
    | SqlFlexible -> "TEXT"

  let parseDefaultExpr (defaultSql: string) : Expr =
    let trimmed = defaultSql.Trim()

    match Int32.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture) with
    | true, number -> Integer number
    | _ ->
      match Double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture) with
      | true, number -> Real number
      | _ when trimmed.StartsWith "'" && trimmed.EndsWith "'" && trimmed.Length >= 2 ->
        let content = trimmed.[1 .. trimmed.Length - 2].Replace("''", "'")
        String content
      | _ -> Value trimmed

  let parseFkAction (rawAction: string) : FkAction option =
    match rawAction.Trim().ToUpperInvariant() with
    | "CASCADE" -> Some Cascade
    | "RESTRICT" -> Some Restrict
    | "NO ACTION" -> Some NoAction
    | "SET NULL" -> Some SetNull
    | "SET DEFAULT" -> Some SetDefault
    | _ -> None

  let fkActionSql (action: FkAction) : string =
    match action with
    | Cascade -> "CASCADE"
    | Restrict -> "RESTRICT"
    | NoAction -> "NO ACTION"
    | SetNull -> "SET NULL"
    | SetDefault -> "SET DEFAULT"
