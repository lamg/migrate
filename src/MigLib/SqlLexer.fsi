module internal migrate.DeclarativeMigrations.SqlLexer

open FSharp.Text.Lexing
open migrate.DeclarativeMigrations.SqlParser

/// Rule token
val token: lexbuf: LexBuffer<char> -> token
