# Build parser

- Download Antlr4 https://www.antlr.org/download.html

Generate parser:

```sh
alias antlr='java -jar antlr-4.7-complete.jar'
antlr -Dlanguage=Go -package sqlite_parser SQLiteLexer.g4
antlr -Dlanguage=Go -visitor  -no-listener -package sqlite_parser SQLiteParser.g4
```

Clean generated code:

```sh
rm *.interp *.tokens *.go
```

More examples at https://blog.gopheracademy.com/advent-2017/parsing-with-antlr4-and-go/
