module internal migrate.DeclarativeMigrations.SqlParserWrapper

open FSharp.Text.Lexing
open migrate.DeclarativeMigrations.Types

let parseSqlFile (fileName: string, sql: string) : Result<SqlFile, string> =
  try
    let lexbuf = LexBuffer<char>.FromString sql
    let result = SqlParser.file SqlLexer.token lexbuf
    Result.Ok result
  with ex ->
    Result.Error $"Parse error in {fileName}: {ex.Message}"
