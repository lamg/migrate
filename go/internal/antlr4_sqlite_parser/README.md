# Build parser

- Download Antlr4 https://www.antlr.org/download.html

```sh
alias antlr='java -jar antlr-4.7-complete.jar'
antlr -Dlanguage=Go SQLiteLexer.g4
antlr -Dlanguage=Go SQLiteParser.g4
```
