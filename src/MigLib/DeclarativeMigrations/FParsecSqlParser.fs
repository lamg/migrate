module internal migrate.DeclarativeMigrations.FParsecSqlParser

open FParsec
open Types

// Parser helpers
let ws = spaces
let ws1 = spaces1
let str s = pstring s
let str_ws s = pstring s .>> ws
let str_ws1 s = pstring s .>> ws1

let keywords =
  [ "CREATE"; "TABLE"; "VIEW"; "INDEX"; "TRIGGER"
    "PRIMARY"; "KEY"; "FOREIGN"; "REFERENCES"; "UNIQUE"
    "NOT"; "NULL"; "DEFAULT"; "CHECK"; "AUTOINCREMENT"
    "INTEGER"; "TEXT"; "REAL"; "TIMESTAMP"; "STRING"
    "IF"; "EXISTS"; "ON"; "AS"; "SELECT"; "FROM"
    "WHERE"; "GROUP"; "ORDER"; "LIMIT"; "UNION" ]
  |> Set.ofList

// Identifier parsing - handles quoted and unquoted identifiers
let identifier : Parser<string, unit> =
  let quotedId quote =
    between (pchar quote) (pchar quote) (manySatisfy (fun c -> c <> quote))

  let unquotedId =
    let isIdentifierFirstChar c = isLetter c || c = '_'
    let isIdentifierChar c = isLetter c || isDigit c || c = '_'

    parse {
      let! first = satisfy isIdentifierFirstChar
      let! rest = manySatisfy isIdentifierChar
      let id = string first + rest
      // Check it's not a keyword
      if Set.contains (id.ToUpper()) keywords then
        return! fail "Identifier cannot be a reserved keyword"
      else
        return id
    }

  choice [
    quotedId '"'
    quotedId '`'
    quotedId '\''
    between (pchar '[') (pchar ']') (manySatisfy (fun c -> c <> ']'))
    unquotedId
  ] .>> ws

// SQL type parsing
let sqlType : Parser<SqlType, unit> =
  let typeParser name sqlType = str_ws1 name >>% sqlType

  choice [
    typeParser "INTEGER" SqlInteger
    typeParser "TEXT" SqlText
    typeParser "REAL" SqlReal
    typeParser "TIMESTAMP" SqlTimestamp
    typeParser "STRING" SqlString
  ] <|> (preturn SqlFlexible)

// Expression parsing (simplified - stores as Value for now)
let expression : Parser<Expr, unit> =
  let quotedString = between (pchar '\'') (pchar '\'') (manySatisfy (fun c -> c <> '\''))
  let number = pint32

  choice [
    quotedString |>> (fun s -> Value s)
    number |>> Integer
    manySatisfy (fun c -> c <> ',' && c <> ')') |>> (fun s -> Value (s.Trim()))
  ] .>> ws

// Column constraint parsing
let columnConstraint : Parser<ColumnConstraint, unit> =
  let notNull = str_ws1 "NOT" >>. str_ws "NULL" >>% NotNull

  let primaryKey =
    parse {
      do! str_ws1 "PRIMARY" >>. str_ws "KEY"
      let! isAuto = opt (str_ws "AUTOINCREMENT")
      return PrimaryKey { constraintName = None; columns = []; isAutoincrement = isAuto.IsSome }
    }

  let unique = str_ws "UNIQUE" >>% Unique []

  let defaultValue =
    parse {
      do! str_ws1 "DEFAULT"
      let! expr = expression
      return Default expr
    }

  let check =
    parse {
      do! str_ws1 "CHECK"
      do! str_ws "("
      let! tokens = manyTill anyChar (str_ws ")")
      return Check (tokens |> List.map string)
    }

  let foreignKey =
    parse {
      do! str_ws1 "REFERENCES"
      let! refTable = identifier
      let! refCols =
        opt (between (str_ws "(") (str_ws ")")
                     (sepBy1 identifier (str_ws ",")))
      return ForeignKey {
        columns = []
        refTable = refTable
        refColumns = refCols |> Option.defaultValue []
      }
    }

  choice [
    notNull
    attempt primaryKey
    unique
    attempt defaultValue
    attempt check
    foreignKey
  ]

// Column definition parsing
let columnDef : Parser<ColumnDef, unit> =
  parse {
    let! name = identifier
    let! colType = sqlType
    let! constraints = many (attempt columnConstraint)
    return { name = name; columnType = colType; constraints = constraints }
  }

// Table constraint parsing
let tableConstraint : Parser<ColumnConstraint, unit> =
  let primaryKey =
    parse {
      do! str_ws1 "PRIMARY" >>. str_ws "KEY"
      do! str_ws "("
      let! cols = sepBy1 identifier (str_ws ",")
      do! str_ws ")"
      return PrimaryKey {
        constraintName = None
        columns = cols
        isAutoincrement = false
      }
    }

  let foreignKey =
    parse {
      do! str_ws1 "FOREIGN" >>. str_ws "KEY"
      do! str_ws "("
      let! cols = sepBy1 identifier (str_ws ",")
      do! str_ws ")"
      do! str_ws1 "REFERENCES"
      let! refTable = identifier
      let! refCols =
        opt (between (str_ws "(") (str_ws ")")
                     (sepBy1 identifier (str_ws ",")))
      return ForeignKey {
        columns = cols
        refTable = refTable
        refColumns = refCols |> Option.defaultValue []
      }
    }

  let unique =
    parse {
      do! str_ws "UNIQUE"
      do! str_ws "("
      let! cols = sepBy1 identifier (str_ws ",")
      do! str_ws ")"
      return Unique cols
    }

  choice [
    attempt primaryKey
    attempt foreignKey
    unique
  ]

// CREATE TABLE parsing
let createTable : Parser<CreateTable, unit> =
  str_ws1 "CREATE" >>. str_ws1 "TABLE" >>.
  opt (str_ws1 "IF" >>. str_ws1 "NOT" >>. str_ws1 "EXISTS") >>.
  identifier .>>. (str_ws "(" >>.
    sepBy1
      (choice [
        attempt (tableConstraint |>> Choice2Of2)
        (columnDef |>> Choice1Of2)
      ])
      (str_ws ",")
    .>> str_ws ")"
    .>> opt (str_ws ";"))
  |>> fun (tableName, items) ->
    let columns = items |> List.choose (function Choice1Of2 c -> Some c | _ -> None)
    let constraints = items |> List.choose (function Choice2Of2 c -> Some c | _ -> None)
    { name = tableName; columns = columns; constraints = constraints }

// CREATE VIEW parsing
let createView : Parser<CreateView, unit> =
  str_ws1 "CREATE" >>.
  opt (str_ws1 "TEMPORARY" <|> str_ws1 "TEMP") >>.
  str_ws1 "VIEW" >>.
  opt (str_ws1 "IF" >>. str_ws1 "NOT" >>. str_ws1 "EXISTS") >>.
  identifier .>>. (str_ws1 "AS" >>.
    many1Satisfy (fun c -> c <> ';') .>>
    opt (str_ws ";"))
  |>> fun (viewName, sql) ->
    { name = viewName
      sqlTokens = [sql.Trim()]
      dependencies = [] }

// CREATE INDEX parsing
let createIndex : Parser<CreateIndex, unit> =
  str_ws1 "CREATE" >>.
  opt (str_ws "UNIQUE") >>.
  str_ws1 "INDEX" >>.
  opt (str_ws1 "IF" >>. str_ws1 "NOT" >>. str_ws1 "EXISTS") >>.
  identifier .>>. (str_ws1 "ON" >>.
    identifier .>>. (str_ws "(" >>.
      sepBy1 identifier (str_ws ",") .>>
      str_ws ")" .>>
      opt (str_ws ";")))
  |>> fun (indexName, (tableName, cols)) ->
    { name = indexName; table = tableName; columns = cols }

// CREATE TRIGGER parsing
let createTrigger : Parser<CreateTrigger, unit> =
  str_ws1 "CREATE" >>.
  opt (str_ws1 "TEMPORARY" <|> str_ws1 "TEMP") >>.
  str_ws1 "TRIGGER" >>.
  opt (str_ws1 "IF" >>. str_ws1 "NOT" >>. str_ws1 "EXISTS") >>.
  identifier .>>. (many1Satisfy (fun c -> c <> ';') .>>
    opt (str_ws ";"))
  |>> fun (triggerName, sql) ->
    // Extract table name from ON clause
    let onMatch = System.Text.RegularExpressions.Regex.Match(sql, @"ON\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
    let table = if onMatch.Success then onMatch.Groups.[1].Value else ""
    { name = triggerName
      sqlTokens = [sql.Trim()]
      dependencies = if System.String.IsNullOrEmpty table then [] else [table] }

// Statement parsing
type Statement =
  | TableStmt of CreateTable
  | ViewStmt of CreateView
  | IndexStmt of CreateIndex
  | TriggerStmt of CreateTrigger

let statement : Parser<Statement, unit> =
  ws >>. choice [
    createTable |>> TableStmt
    attempt createView |>> ViewStmt
    attempt createIndex |>> IndexStmt
    attempt createTrigger |>> TriggerStmt
  ] .>> ws

// Parse multiple statements
let statements : Parser<Statement list, unit> =
  many statement .>> eof

// Main parse function
let parse (fileName: string, sql: string) : Result<SqlFile, string> =
  match run statements sql with
  | Success(stmts, _, _) ->
    let mutable file = emptyFile

    for stmt in stmts do
      match stmt with
      | TableStmt t -> file <- { file with tables = t :: file.tables }
      | ViewStmt v -> file <- { file with views = v :: file.views }
      | IndexStmt i -> file <- { file with indexes = i :: file.indexes }
      | TriggerStmt t -> file <- { file with triggers = t :: file.triggers }

    Ok file
  | Failure(errorMsg, _, _) ->
    Error $"Parse error in {fileName}: {errorMsg}"
