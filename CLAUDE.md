# Claude Code Guidelines for migrate Project

## F# Coding Conventions

### Method Call Style - Single Argument Functions

When calling methods with a single argument, use function syntax instead of parenthesized syntax:

**Preferred:**
```fsharp
upper.Contains "NOT NULL"
str.StartsWith "CREATE"
Default <| Value ""
```

**Avoid:**
```fsharp
upper.Contains("NOT NULL")
str.StartsWith("CREATE")
Default(Value "")
```

This applies to all single-argument method calls and improves readability and follows idiomatic F# conventions.

### Related Methods
- `.Contains`
- `.StartsWith`
- `.EndsWith`
- `.Trim`
- `.ToUpper`
- `.ToLower`
- Any other single-argument method or function

## Project Architecture

### Parser Implementation
- The project uses a **pure F# regex-based parser** (SqlParser.fs) instead of ANTLR4
- FParsec dependency is available but not currently used for full SQL parsing
- Regex patterns handle SQL parsing with proper constraint and dependency extraction

### Key Files
- `src/MigLib/DeclarativeMigrations/SqlParser.fs` - SQL parsing implementation
- `src/Directory.Packages.props` - Central package management (no ANTLR4 references)

## Testing
All changes should pass the existing test suite:
```bash
cd src && dotnet test
```

Ensure all 3 tests pass (TableMigration, UseAsLib, ViewMigration)
