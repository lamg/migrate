module internal MigLib.Schema.Sql

open MigLib.Schema.Types

let quoteIdentifier (identifier: string) =
  let escaped = identifier.Replace("\"", "\"\"")
  $"\"{escaped}\""

let sqlTypeStorageName =
  function
  | SqlInteger -> "INTEGER"
  | SqlText -> "TEXT"
  | SqlReal -> "REAL"
  | SqlTimestamp -> "TEXT"
  | SqlString -> "TEXT"

let sqlTypeDisplayName =
  function
  | SqlInteger -> "INTEGER"
  | SqlText -> "TEXT"
  | SqlReal -> "REAL"
  | SqlTimestamp -> "TIMESTAMP"
  | SqlString -> "STRING"

let parseDeclaredSqlType (declaredType: string) =
  let normalized = declaredType.Trim().ToUpperInvariant()

  if normalized.Contains "INT" then
    SqlInteger
  elif
    normalized.Contains "REAL"
    || normalized.Contains "FLOA"
    || normalized.Contains "DOUB"
  then
    SqlReal
  elif
    normalized.Contains "TEXT"
    || normalized.Contains "CHAR"
    || normalized.Contains "CLOB"
  then
    SqlText
  elif normalized.Contains "TIME" || normalized.Contains "DATE" then
    SqlTimestamp
  else
    SqlString

let fkActionSql =
  function
  | Cascade -> "CASCADE"
  | Restrict -> "RESTRICT"
  | NoAction -> "NO ACTION"
  | SetNull -> "SET NULL"
  | SetDefault -> "SET DEFAULT"
