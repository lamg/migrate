module internal migrate.DeclarativeMigrations.FParsecSqlParser

open FParsec
open Types

// Parser helpers
let ws = spaces
let ws1 = spaces1
let str s = pstringCI s // Case-insensitive
let str_ws s = pstringCI s .>> ws // Case-insensitive
let str_ws1 s = pstringCI s .>> ws1 // Case-insensitive
let keyword s = str_ws1 s >>% ()
let terminal s = str_ws s >>% ()
let opar = terminal "("
let cpar = terminal ")"
let comma = str_ws ","
let semi = str_ws ";"

let keywords =
  [ "CREATE"
    "TABLE"
    "VIEW"
    "INDEX"
    "TRIGGER"
    "PRIMARY"
    "KEY"
    "FOREIGN"
    "REFERENCES"
    "UNIQUE"
    "NOT"
    "NULL"
    "DEFAULT"
    "CHECK"
    "AUTOINCREMENT"
    "INTEGER"
    "TEXT"
    "REAL"
    "TIMESTAMP"
    "STRING"
    "IF"
    "EXISTS"
    "ON"
    "AS"
    "SELECT"
    "FROM"
    "WHERE"
    "GROUP"
    "ORDER"
    "LIMIT"
    "UNION"
    "INSERT"
    "INTO"
    "VALUES" ]
  |> Set.ofList

// Identifier parsing - handles quoted and unquoted identifiers
let identifier: Parser<string, unit> =
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

  choice
    [ quotedId '"'
      quotedId '`'
      quotedId '\''
      between (pchar '[') (pchar ']') (manySatisfy (fun c -> c <> ']'))
      unquotedId ]
  .>> ws

// Optional identifier list in parentheses
let optIdentifierList = opt (between opar cpar (sepBy1 identifier comma))

// SQL type parsing
let sqlType: Parser<SqlType, unit> =
  let typeParser name sqlType = str_ws name >>% sqlType // Use str_ws instead of str_ws1 to allow no whitespace before comma

  choice
    [ typeParser "INTEGER" SqlInteger
      typeParser "TEXT" SqlText
      typeParser "REAL" SqlReal
      typeParser "TIMESTAMP" SqlTimestamp
      typeParser "STRING" SqlString ]
  <|> preturn SqlFlexible

// Expression parsing
let expression: Parser<Expr, unit> =
  let quotedString =
    between (pchar '\'') (pchar '\'') (manySatisfy (fun c -> c <> '\'')) |>> String

  let floatingPoint = pfloat |>> Real

  let integer = pint32 |>> Integer

  // Parse parenthesized expression with balanced parens (e.g., DEFAULT (strftime(...)))
  let parenthesizedExpr: Parser<Expr, unit> =
    let rec balancedContent depth acc =
      parse {
        let! c = anyChar

        match c with
        | '(' -> return! balancedContent (depth + 1) (acc + "(")
        | ')' when depth > 1 -> return! balancedContent (depth - 1) (acc + ")")
        | ')' -> return acc + ")"
        | '\'' ->
          // Handle quoted strings inside parens
          let! quoted = manySatisfy (fun ch -> ch <> '\'')
          do! pchar '\'' |>> ignore
          return! balancedContent depth (acc + "'" + quoted + "'")
        | _ -> return! balancedContent depth (acc + string c)
      }

    pchar '(' >>= fun _ -> balancedContent 1 "(" |>> Value

  let unquotedValue =
    manySatisfy (fun c -> c <> ',' && c <> ')' && c <> ';')
    |>> fun s -> Value(s.Trim())

  choice
    [ attempt quotedString
      attempt floatingPoint
      attempt integer
      attempt parenthesizedExpr
      unquotedValue ]
  .>> ws

// Column constraint parsing
let columnConstraint: Parser<ColumnConstraint, unit> =
  let notNull = str_ws1 "NOT" >>. str_ws "NULL" >>% NotNull

  let primaryKey =
    str_ws1 "PRIMARY" >>. str_ws "KEY" >>. opt (str_ws "AUTOINCREMENT")
    |>> (fun isAuto ->
      PrimaryKey
        { constraintName = None
          columns = []
          isAutoincrement = isAuto.IsSome })

  let unique = str_ws "UNIQUE" >>% Unique []

  let defaultValue =
    parse {
      do! keyword "DEFAULT"
      let! expr = expression
      return Default expr
    }

  let check =
    parse {
      do! keyword "CHECK"
      do! opar
      let! tokens = manyTill anyChar cpar
      return Check(tokens |> List.map string)
    }

  let foreignKey =
    parse {
      do! keyword "REFERENCES"
      let! refTable = identifier
      let! refCols = optIdentifierList

      return
        ForeignKey
          { columns = []
            refTable = refTable
            refColumns = refCols |> Option.defaultValue [] }
    }

  choice
    [ notNull
      attempt primaryKey
      unique
      attempt defaultValue
      attempt check
      foreignKey ]

// Column definition parsing
let columnDef: Parser<ColumnDef, unit> =
  parse {
    let! name = identifier
    let! colType = sqlType
    let! constraints = many (attempt columnConstraint)

    return
      { name = name
        columnType = colType
        constraints = constraints }
  }

// Table constraint parsing
let tableConstraint: Parser<ColumnConstraint, unit> =
  let primaryKey =
    parse {
      do! str_ws1 "PRIMARY" >>. str_ws "KEY" >>% ()
      do! opar
      let! cols = sepBy1 identifier comma
      do! cpar

      return
        PrimaryKey
          { constraintName = None
            columns = cols
            isAutoincrement = false }
    }

  let foreignKey =
    parse {
      do! str_ws1 "FOREIGN" >>. str_ws "KEY" >>% ()
      do! opar
      let! cols = sepBy1 identifier comma
      do! cpar
      do! keyword "REFERENCES"
      let! refTable = identifier
      let! refCols = optIdentifierList

      return
        ForeignKey
          { columns = cols
            refTable = refTable
            refColumns = refCols |> Option.defaultValue [] }
    }

  let unique =
    parse {
      do! terminal "UNIQUE"
      do! opar
      let! cols = sepBy1 identifier comma
      do! cpar
      return Unique cols
    }

  choice [ attempt primaryKey; attempt foreignKey; unique ]

// QueryBy annotation parsing
let queryByAnnotation: Parser<QueryByAnnotation, unit> =
  pstring "--"
  >>. spaces
  >>. pstringCI "QueryBy"
  >>. spaces
  >>. pchar '('
  >>. spaces
  >>. sepBy1 identifier (pchar ',' >>. spaces)
  .>> spaces
  .>> pchar ')'
  .>> restOfLine false
  |>> fun cols -> { columns = cols }

let queryByAnnotations: Parser<QueryByAnnotation list, unit> =
  many (attempt (ws >>. queryByAnnotation))

// QueryByOrCreate annotation parsing
let queryByOrCreateAnnotation: Parser<QueryByOrCreateAnnotation, unit> =
  pstring "--"
  >>. spaces
  >>. pstringCI "QueryByOrCreate"
  >>. spaces
  >>. pchar '('
  >>. spaces
  >>. sepBy1 identifier (pchar ',' >>. spaces)
  .>> spaces
  .>> pchar ')'
  .>> restOfLine false
  |>> fun cols -> { columns = cols }

let queryByOrCreateAnnotations: Parser<QueryByOrCreateAnnotation list, unit> =
  many (attempt (ws >>. queryByOrCreateAnnotation))

// InsertOrIgnore annotation parsing
let insertOrIgnoreAnnotation: Parser<InsertOrIgnoreAnnotation, unit> =
  pstring "--"
  >>. spaces
  >>. pstringCI "InsertOrIgnore"
  >>. spaces
  >>. opt (pchar '(' >>. spaces >>. pchar ')')
  .>> restOfLine false
  >>% InsertOrIgnoreAnnotation

let insertOrIgnoreAnnotations: Parser<InsertOrIgnoreAnnotation list, unit> =
  many (attempt (ws >>. insertOrIgnoreAnnotation))

type Annotation =
  | QueryByAnno of QueryByAnnotation
  | QueryByOrCreateAnno of QueryByOrCreateAnnotation
  | InsertOrIgnoreAnno of InsertOrIgnoreAnnotation

let annotation: Parser<Annotation, unit> =
  choice
    [ attempt (queryByAnnotation |>> QueryByAnno)
      attempt (queryByOrCreateAnnotation |>> QueryByOrCreateAnno)
      insertOrIgnoreAnnotation |>> InsertOrIgnoreAnno ]

let annotations: Parser<Annotation list, unit> = many (attempt (ws >>. annotation))

let commentLine: Parser<string, unit> = pstring "--" >>. restOfLine false

let commentLines: Parser<string list, unit> = many (attempt (ws >>. commentLine))

let parseAnnotationsFromComments (comments: string list) =
  let queryBy = ResizeArray<QueryByAnnotation>()
  let queryByOrCreate = ResizeArray<QueryByOrCreateAnnotation>()
  let insertOrIgnore = ResizeArray<InsertOrIgnoreAnnotation>()

  let parseColumns (cols: string) =
    cols.Split(',')
    |> Array.map (fun c -> c.Trim())
    |> Array.filter (fun c -> c <> "")
    |> Array.toList

  let tryMatch (pattern: string) (line: string) =
    let m =
      System.Text.RegularExpressions.Regex.Match(
        line.Trim(),
        pattern,
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
      )

    if m.Success then Some m else None

  for line in comments do
    match tryMatch @"^QueryBy\s*\((.*)\)\s*$" line with
    | Some m ->
      let cols = parseColumns m.Groups.[1].Value

      if cols.Length > 0 then
        queryBy.Add({ columns = cols })
    | None ->
      match tryMatch @"^QueryByOrCreate\s*\((.*)\)\s*$" line with
      | Some m ->
        let cols = parseColumns m.Groups.[1].Value

        if cols.Length > 0 then
          queryByOrCreate.Add({ columns = cols })
      | None ->
        match tryMatch @"^InsertOrIgnore(?:\s*\(\s*\))?\s*$" line with
        | Some _ -> insertOrIgnore.Add(InsertOrIgnoreAnnotation)
        | None -> ()

  (List.ofSeq queryBy, List.ofSeq queryByOrCreate, List.ofSeq insertOrIgnore)

// CREATE TABLE parsing
let createTable: Parser<CreateTable, unit> =
  str_ws1 "CREATE"
  >>. str_ws1 "TABLE"
  >>. opt (str_ws1 "IF" >>. str_ws1 "NOT" >>. str_ws1 "EXISTS")
  >>. identifier
  .>>. (opar
        >>. sepBy1 (choice [ attempt (tableConstraint |>> Choice2Of2); columnDef |>> Choice1Of2 ]) comma
        .>> cpar
        .>> opt semi)
  .>>. commentLines
  |>> fun (((tableName, items), comments)) ->
    let columns =
      items
      |> List.choose (function
        | Choice1Of2 c -> Some c
        | _ -> None)

    let constraints =
      items
      |> List.choose (function
        | Choice2Of2 c -> Some c
        | _ -> None)

    let queryByAnnos, queryByOrCreateAnnos, insertOrIgnoreAnnos =
      parseAnnotationsFromComments comments

    { name = tableName
      columns = columns
      constraints = constraints
      queryByAnnotations = queryByAnnos
      queryByOrCreateAnnotations = queryByOrCreateAnnos
      insertOrIgnoreAnnotations = insertOrIgnoreAnnos }

// CREATE VIEW parsing
let createView: Parser<CreateView, unit> =
  let createPart =
    str_ws1 "CREATE"
    >>. opt (str_ws1 "TEMPORARY" <|> str_ws1 "TEMP")
    >>. str_ws1 "VIEW"
    >>. opt (str_ws1 "IF" >>. str_ws1 "NOT" >>. str_ws1 "EXISTS")

  createPart >>. identifier
  .>>. (str_ws1 "AS" >>. many1Satisfy (fun c -> c <> ';') .>> opt semi)
  .>>. commentLines
  |>> fun (((viewName, selectPart), comments)) ->
    // Build the full CREATE VIEW statement
    let fullStatement = $"CREATE VIEW {viewName} AS {selectPart.Trim()}"

    // Extract table names from FROM and JOIN clauses
    let fromMatches =
      System.Text.RegularExpressions.Regex.Matches(
        selectPart,
        @"(?:FROM|JOIN)\s+(\w+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
      )

    let dependencies =
      [ for m in fromMatches do
          yield m.Groups.[1].Value ]
      |> List.distinct

    let queryByAnnos, queryByOrCreateAnnos, insertOrIgnoreAnnos =
      parseAnnotationsFromComments comments

    { name = viewName
      sqlTokens = [ fullStatement ]
      dependencies = dependencies
      queryByAnnotations = queryByAnnos
      queryByOrCreateAnnotations = queryByOrCreateAnnos
      insertOrIgnoreAnnotations = insertOrIgnoreAnnos }

// CREATE INDEX parsing
let createIndex: Parser<CreateIndex, unit> =
  str_ws1 "CREATE"
  >>. opt (str_ws "UNIQUE")
  >>. str_ws1 "INDEX"
  >>. opt (str_ws1 "IF" >>. str_ws1 "NOT" >>. str_ws1 "EXISTS")
  >>. identifier
  .>>. (str_ws1 "ON" >>. identifier
        .>>. (opar >>. sepBy1 identifier comma .>> cpar .>> opt semi))
  |>> fun (indexName, (tableName, cols)) ->
    { name = indexName
      table = tableName
      columns = cols }

// CREATE TRIGGER parsing
let createTrigger: Parser<CreateTrigger, unit> =
  str_ws1 "CREATE"
  >>. opt (str_ws1 "TEMPORARY" <|> str_ws1 "TEMP")
  >>. str_ws1 "TRIGGER"
  >>. opt (str_ws1 "IF" >>. str_ws1 "NOT" >>. str_ws1 "EXISTS")
  >>. identifier
  .>>. (many1Satisfy (fun c -> c <> ';') .>> opt semi)
  |>> fun (triggerName, sql) ->
    // Extract table name from ON clause
    let onMatch =
      System.Text.RegularExpressions.Regex.Match(
        sql,
        @"ON\s+(\w+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
      )

    let table = if onMatch.Success then onMatch.Groups.[1].Value else ""

    { name = triggerName
      sqlTokens = [ sql.Trim() ]
      dependencies = if System.String.IsNullOrEmpty table then [] else [ table ] }

// INSERT INTO parsing
let insertInto: Parser<InsertInto, unit> =
  keyword "INSERT" >>. keyword "INTO" >>. identifier
  .>>. (opar >>. sepBy1 identifier comma .>> cpar)
  .>>. (keyword "VALUES"
        >>. sepBy1 (opar >>. sepBy1 expression comma .>> cpar) (comma .>> ws))
  .>> opt semi
  |>> fun (((tableName, columnNames), valueLists)) ->
    { table = tableName
      columns = columnNames
      values = valueLists }

// Standalone comment parsing (comments not associated with QueryBy annotations)
let standaloneComment: Parser<unit, unit> = pstring "--" >>. restOfLine true >>% ()

// Skip whitespace and comments (interleaved)
let wsAndComments: Parser<unit, unit> =
  spaces >>. skipMany (standaloneComment >>. spaces)

// Statement parsing
type Statement =
  | TableStmt of CreateTable
  | ViewStmt of CreateView
  | IndexStmt of CreateIndex
  | TriggerStmt of CreateTrigger
  | InsertStmt of InsertInto

let statementContent: Parser<Statement, unit> =
  choice
    [ attempt createTable |>> TableStmt
      attempt createView |>> ViewStmt
      attempt createIndex |>> IndexStmt
      attempt createTrigger |>> TriggerStmt
      attempt insertInto |>> InsertStmt ]

let statement: Parser<Statement, unit> = wsAndComments >>. statementContent .>> ws

// Parse multiple statements
let statements: Parser<Statement list, unit> =
  many (attempt statement) .>> wsAndComments .>> eof

// Improve error messages for common mistakes
let improveErrorMessage (sql: string) (errorMsg: string) : string =
  // Detect trailing comma before closing parenthesis
  if errorMsg.Contains "Expecting:" && errorMsg.Contains "')'" |> not then
    // Look for pattern: comma followed by whitespace/newlines and then );
    let trailingCommaPattern = System.Text.RegularExpressions.Regex @",\s*\);"
    let matches = trailingCommaPattern.Matches sql

    if matches.Count > 0 then
      let firstMatch = matches.[0]
      // Find line number
      let lineNumber = sql.Substring(0, firstMatch.Index).Split('\n').Length
      $"{errorMsg}\n\nHint: Found trailing comma before ');' near line {lineNumber}. Remove the comma before the closing parenthesis."
    else
      errorMsg
  else
    errorMsg

// Main parse function
let parseSqlFile (fileName: string, sql: string) : Result<SqlFile, string> =
  match run statements sql with
  | Success(stmts, _, _) ->
    let mutable file = emptyFile

    for stmt in stmts do
      match stmt with
      | TableStmt t -> file <- { file with tables = t :: file.tables }
      | ViewStmt v -> file <- { file with views = v :: file.views }
      | IndexStmt i ->
        file <-
          { file with
              indexes = i :: file.indexes }
      | TriggerStmt t ->
        file <-
          { file with
              triggers = t :: file.triggers }
      | InsertStmt i ->
        file <-
          { file with
              inserts = i :: file.inserts }

    Result.Ok file
  | Failure(errorMsg, _, _) ->
    let improvedMsg = improveErrorMessage sql errorMsg
    Result.Error $"Parse error in {fileName}: {improvedMsg}"
