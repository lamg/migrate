module internal migrate.Execution.FormatSql

open System.Text.RegularExpressions
open SqlPrettify

let sqliteKeywords =
  [ "ABORT"
    "ACTION"
    "ADD"
    "AGGREGATE"
    "ALL"
    "ALTER"
    "ANALYZE"
    "AND"
    "AS"
    "ASC"
    "ATTACH"
    "AUTOINCREMENT"
    "BEFORE"
    "BEGIN"
    "BETWEEN"
    "BY"
    "CASCADE"
    "CASE"
    "CAST"
    "CHECK"
    "COLLATE"
    "COLUMN"
    "COMMIT"
    "CONFLICT"
    "CONSTRAINT"
    "CREATE"
    "CROSS"
    "CURRENT"
    "CURRENT_DATE"
    "CURRENT_TIME"
    "CURRENT_TIMESTAMP"
    "DATABASE"
    "DEFAULT"
    "DEFERRABLE"
    "DEFERRED"
    "DELETE"
    "DESC"
    "DETACH"
    "DISTINCT"
    "DO"
    "DROP"
    "EACH"
    "ELSE"
    "END"
    "EXCEPT"
    "EXCLUSIVE"
    "EXISTS"
    "EXPLAIN"
    "FAIL"
    "FOR"
    "FOREIGN"
    "FROM"
    "FULL"
    "GLOB"
    "GROUP"
    "HAVING"
    "IF"
    "IGNORE"
    "IMMEDIATE"
    "IN"
    "INDEX"
    "INDEXED"
    "INITIALLY"
    "INNER"
    "INSERT"
    "INSTEAD"
    "INTERSECT"
    "INTERVAL"
    "INTO"
    "IS"
    "ISNULL"
    "JOIN"
    "KEY"
    "LEFT"
    "LIKE"
    "LIMIT"
    "MATCH"
    "NATURAL"
    "NO"
    "NOT"
    "NOTNULL"
    "NULL"
    "OF"
    "OFFSET"
    "ON"
    "OR"
    "ORDER"
    "OUTER"
    "PLAN"
    "PRAGMA"
    "PRIMARY"
    "QUERY"
    "RAISE"
    "REFERENCES"
    "REPLACE"
    "RESTRICT"
    "ROLLBACK"
    "ROW"
    "SAVEPOINT"
    "SELECT"
    "SET"
    "TABLE"
    "TEMP"
    "TEMPORARY"
    "THEN"
    "TO"
    "TRANSACTION"
    "TRIGGER"
    "UNION"
    "UNIQUE"
    "UPDATE"
    "USING"
    "VACUUM"
    "VALUES"
    "VIEW"
    "VIRTUAL"
    "WHEN"
    "WHERE"
    "WITH"
    "WITHOUT" ]

let ansiGreen = "\x1b[32m"
let ansiBlue = "\u001b[34m"
let ansiPurple = "\u001b[35m"


let colorizeSymbols color xs sql =
  let pattern = xs |> String.concat "|"
  let blankSymbol = $@"\b({pattern})\b"

  let matchEvaluator (m: Match) =
    let ansiReset = "\x1b[0m"
    $"%s{color}%s{m.Value}%s{ansiReset}"

  Regex.Replace(sql, blankSymbol, matchEvaluator, RegexOptions.IgnoreCase)

let colorizeKeywords = colorizeSymbols ansiBlue sqliteKeywords

let colorizeSpecialIds =
  let ids = [ "integer"; "text" ]
  colorizeSymbols ansiPurple ids

let colorize = colorizeSpecialIds >> colorizeKeywords

let keywordToUpper (input: string) =
  let rec replaceAux acc =
    function
    | [] -> acc
    | term: string :: rest ->
      let term = $"^{term} | {term} "
      let pattern = Regex.Escape term
      let regex = Regex(pattern, RegexOptions.IgnoreCase)
      let updatedAcc = regex.Replace(acc, term.ToUpper())
      replaceAux updatedAcc rest

  replaceAux input sqliteKeywords

let join (xs: string list) = xs |> String.concat ";\n\n"

let pretty = keywordToUpper >> SqlPrettify.Pretty >> _.TrimEnd()

let joinPretty xs = xs |> List.map pretty |> join

let format (withColors: bool) (sql: string) =
  sql |> pretty |> (if withColors then colorize else id)

let formatSeq (withColors: bool) (statements: string seq) =
  statements |> Seq.map (format withColors) |> String.concat ";\n\n"
