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

### Module Declaration Style - Module Per File

When creating a file with a single module, use module-per-file syntax instead of namespace with nested module:

**Preferred:**
```fsharp
/// Documentation comment for the module
module migrate.Db

open System
open Microsoft.Data.Sqlite

let someFunction x = x + 1
```

**Avoid:**
```fsharp
namespace migrate

open System
open Microsoft.Data.Sqlite

module Db =
  let someFunction x = x + 1
```

This applies to all single-module files in the project and follows modern F# conventions for cleaner, less-indented code.

### Code Formatting

All F# code must be formatted using [Fantomas](https://fsprojects.github.io/fantomas/) before committing.

**Format all files in the project:**
```bash
cd src
dotnet fantomas .
```

**Format specific files:**
```bash
dotnet fantomas src/MigLib/Db.fs
```

This ensures consistent code style across the entire codebase and prevents formatting-related diffs in commits.

## Testing
All changes should pass the existing test suite:
```bash
cd src && dotnet test
```
