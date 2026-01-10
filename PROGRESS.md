# F# Code Generation Implementation Progress

**Branch:** `fsharp-generation`
**Last Updated:** 2026-01-10
**Status:** Code generation working, FParsec parser in progress

## Overview

Implementing F# code generation feature to transform Migrate from a pure migration tool into a migration tool + lightweight ORM with type-safe F# code generation.

## ✅ Completed Tasks

### 1. Branch Setup
- Created `fsharp-generation` branch
- Added Fabulous.AST 1.2.0 dependency to Directory.Packages.props
- Updated version to 2.0.0 in MigLib.fsproj

### 2. Removed Goose Import Support
- Deleted `src/MigLib/ImportGoose/` directory completely
- Removed ImportGoose references from MigLib.fsproj
- Removed Import command and related args from mig/Program.fs
- Cleaned up CLI to remove all Goose-related functionality

### 3. Created CodeGen Module Structure
Created 6 new modules in `src/MigLib/CodeGen/`:

#### **FabulousAstHelpers.fs**
- Currently just a placeholder with formatCode function
- Initially planned for Fabulous.AST integration, but using string templates for now

#### **TypeGenerator.fs**
- `mapSqlType`: Maps SQL types to F# types (INTEGER→int64, TEXT→string, etc.)
- `isColumnNullable`: Checks if column has NOT NULL constraint
- `generateField`: Generates record field (name, type) with proper capitalization
- `generateRecordType`: Generates complete F# record type from table definition

**Type Mapping:**
```
SqlInteger → int64
SqlText → string
SqlReal → float
SqlTimestamp → DateTime
SqlString → string
SqlFlexible → obj
```

**Null Handling:**
- NOT NULL columns → direct types (e.g., `string`)
- Nullable columns → option types (e.g., `string option`)

#### **QueryGenerator.fs**
- `getPrimaryKey`: Extracts primary key column(s) from table
- `getForeignKeys`: Extracts foreign key relationships
- `capitalize`: Helper for F# naming conventions
- `generateInsert`: Generates INSERT method, returns `Result<int64, SqliteException>`
  - Excludes auto-increment primary keys
  - Returns `last_insert_rowid`
  - Handles nullable columns with `Option.toObj` and `DBNull.Value`
- `generateGet`: Generates GetById method, returns `Result<T option, SqliteException>`
  - Only for tables with single-column primary keys
  - Handles nullable column reads with `IsDBNull` checks
- `generateTableCode`: Orchestrates generation of all methods for a table

**Generated Method Pattern:**
```fsharp
type Student with
    static member Insert(conn: SqliteConnection, item: Student) : Result<int64, SqliteException> =
        try
            use cmd = new SqliteCommand("INSERT INTO students (...) VALUES (...)", conn)
            cmd.Parameters.AddWithValue("@name", item.Name) |> ignore
            cmd.ExecuteNonQuery() |> ignore
            Ok(conn.LastInsertRowId)
        with
        | :? SqliteException as ex -> Error ex

    static member GetById(conn: SqliteConnection, id: int64) : Result<Student option, SqliteException> =
        try
            use cmd = new SqliteCommand("SELECT ... FROM students WHERE id = @id", conn)
            cmd.Parameters.AddWithValue("@id", id) |> ignore
            use reader = cmd.ExecuteReader()
            if reader.Read() then
                Ok(Some { Id = reader.GetInt64(0); ... })
            else
                Ok None
        with
        | :? SqliteException as ex -> Error ex
```

#### **FileMapper.fs**
- `sqlFileToModuleName`: Converts SQL filename to F# module name (students.sql → Students)
- `sqlFileToFSharpFile`: Converts SQL path to F# file path (students.sql → Students.fs)
- `ensureDirectory`: Creates output directory if needed

**Naming Convention:**
- SQL files and F# files colocated in same directory
- First letter capitalized for F# module naming

#### **ProjectGenerator.fs**
- `generateProjectFile`: Generates .fsproj XML content with:
  - Target framework: net10.0
  - Documentation generation enabled
  - Package references: FSharp.Core 9.0.100, FsToolkit.ErrorHandling 4.18.0, Microsoft.Data.Sqlite 9.0.0
- `writeProjectFile`: Writes .fsproj to disk

#### **CodeGen.fs**
- `generateCodeForSqlFile`: Main function for single SQL file
  - Reads SQL file
  - Parses with SqlParser
  - Generates module with imports, record types, and CRUD methods
  - Writes to F# file
- `generateCode`: Processes entire directory
  - Finds all *.sql files
  - Generates F# file for each
  - Creates .fsproj file
  - Returns list of generated files

**Generated Module Structure:**
```fsharp
module Students

open System
open Microsoft.Data.Sqlite
open FsToolkit.ErrorHandling

type Students = {
    Id: int64 option
    Name: string
    Email: string option
    Enrollment_date: DateTime
}

type Students with
    [Insert and GetById methods...]
```

### 4. Added CLI Command
**New command:** `mig codegen`

**Args:**
- `-d, --directory`: Directory containing SQL schema files (defaults to current directory)

**Usage:**
```bash
mig codegen
mig codegen -d ./schema
```

**Output:**
```
Generated files:
  ./MyProject.fsproj
  ./Students.fs
  ./Courses.fs
```

**Implementation in mig/Program.fs:**
- Added `Codegen` to Args discriminated union
- Added `CodegenArgs` with Directory parameter
- Implemented `codegen` function that calls `CodeGen.generateCode`
- Added case in main match expression

### 5. Fixed SQL Parser
**Issue:** Regex parser failing to extract columns from multiline CREATE TABLE statements

**Root Cause:** Pattern `@"\((.*)\)(?:\s*;)?$"` with default regex options doesn't match across newlines (`.` doesn't match `\n`)

**Fix:** Added `RegexOptions.Singleline` flag
```fsharp
Regex.Match(text, @"\((.*)\)", RegexOptions.IgnoreCase ||| RegexOptions.Singleline)
```

**Result:** Parser now correctly extracts:
```sql
CREATE TABLE students (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    email TEXT,
    enrollment_date TIMESTAMP NOT NULL
);
```

All 3 existing tests pass.

### 6. Updated Documentation
**spec.md changes:**
- Updated title to "Database Migration Tool & F# Type Generator"
- Added comprehensive F# Type Generation section (§7) with:
  - Generated code structure
  - Type mapping details
  - CRUD operation signatures
  - Example generated code
- Updated architecture diagram to include CodeGen component
- Added 6 new module documentation sections
- Added 5 new design decision sections:
  - Fabulous.AST for code generation (§7)
  - Static methods for CRUD operations (§8)
  - Result types for error handling (§9)
  - Colocation of SQL and F# files (§10)
  - Raw ADO.NET over micro-ORMs (§11)
- Updated dependencies to include Fabulous.AST
- Updated CLI commands
- Added code generation test strategy
- Added performance considerations for code generation
- Removed Goose import documentation

## 🚧 In Progress

### FParsec-based SQL Parser

**File:** `src/MigLib/DeclarativeMigrations/FParsecSqlParser.fs` (currently commented out in .fsproj)

**Goal:** Replace regex-based parser with robust FParsec parser combinator implementation

**Status:** Skeleton complete, has compilation errors

**Completed Parts:**
```fsharp
// Core parsers
let ws = spaces
let identifier : Parser<string, unit> = ...  // ✅ Complete
let sqlType : Parser<SqlType, unit> = ...    // ✅ Complete
let expression : Parser<Expr, unit> = ...    // ✅ Complete

// Constraint parsers
let columnConstraint : Parser<ColumnConstraint, unit> = ...  // ✅ Partially complete
  - notNull ✅
  - primaryKey ❌ (parse {} syntax errors)
  - unique ✅
  - defaultValue ❌ (parse {} syntax errors)
  - check ❌ (parse {} syntax errors)
  - foreignKey ❌ (parse {} syntax errors)

let tableConstraint : Parser<ColumnConstraint, unit> = ...  // ❌ Not started
let columnDef : Parser<ColumnDef, unit> = ...               // ✅ Complete

// Statement parsers
let createTable : Parser<CreateTable, unit> = ...    // ✅ Converted to combinator style
let createView : Parser<CreateView, unit> = ...      // ✅ Converted to combinator style
let createIndex : Parser<CreateIndex, unit> = ...    // ✅ Converted to combinator style
let createTrigger : Parser<CreateTrigger, unit> = ... // ✅ Converted to combinator style
```

**Current Errors:**
1. **Type mismatches in parse {} blocks** (lines 79, 88, 95, 96, 103):
   - `do!` expects parsers returning `unit`, but we're using parsers that return values
   - Error: "This expression was expected to have type 'string' but here has type 'unit'"

2. **Incomplete pattern matches** (lines 137, 138, 150, 151, 153, 154, 168, 169, 171):
   - Warning about non-exhaustive pattern matching in table constraint parsers

3. **Application errors** (lines 275, 277):
   - "This value is not a function and cannot be applied"
   - Likely in the main parse function

**What Needs Fixing:**

**Option 1 - Convert parse {} to combinator style (RECOMMENDED):**
```fsharp
// ❌ Current (broken):
let primaryKey =
  parse {
    do! str_ws1 "PRIMARY" >>. str_ws "KEY"
    let! isAuto = opt (str_ws "AUTOINCREMENT")
    return PrimaryKey { constraintName = None; columns = []; isAutoincrement = isAuto.IsSome }
  }

// ✅ Fixed:
let primaryKey =
  str_ws1 "PRIMARY" >>. str_ws "KEY" >>.
  opt (str_ws "AUTOINCREMENT")
  |>> (fun isAuto -> PrimaryKey { constraintName = None; columns = []; isAutoincrement = isAuto.IsSome })
```

**Option 2 - Fix parse {} syntax:**
- Use `>>%` for parsers that should discard results
- Ensure proper types for `do!` expressions

**Remaining Work:**
1. Fix all constraint parsers (primaryKey, defaultValue, check, foreignKey)
2. Complete table constraint parsers
3. Test against existing test cases:
   - `CREATE TABLE table0(id integer NOT NULL)`
   - `CREATE TABLE student(id integer NOT NULL, name text NOT NULL)`
   - Foreign key dependencies
4. Handle edge cases:
   - Quoted identifiers
   - IF NOT EXISTS clauses
   - Complex column constraints
5. Replace regex parser once all tests pass

## ⏭️ Next Steps (Priority Order)

### 1. Complete FParsec Parser (High Priority)
- [ ] Fix all parse {} syntax errors by converting to combinator style
- [ ] Complete table constraint parsers
- [ ] Test against all existing test cases
- [ ] Enable FParsecSqlParser.fs in .fsproj
- [ ] Update SqlParser.fs to use FParsec parser
- [ ] Verify all 3 tests still pass
- [ ] Commit working FParsec parser

### 2. Add More CRUD Methods (Medium Priority)
- [ ] Implement `Update` method
- [ ] Implement `Delete` method
- [ ] Implement `GetAll` method
- [ ] Test generated CRUD methods

### 3. Add JOIN Query Generation (Medium Priority)
- [ ] Detect foreign key relationships
- [ ] Generate methods like `GetStudentWithCourses`
- [ ] Handle one-to-many relationships
- [ ] Test JOIN queries

### 4. Add Transaction Support (Medium Priority)
- [ ] Generate `WithTransaction` helper method
- [ ] Add transaction examples to generated code
- [ ] Test transaction rollback scenarios

### 5. Update mig exec (High Priority)
- [ ] Add call to `CodeGen.generateCode` after successful migration
- [ ] Handle errors gracefully
- [ ] Add flag to disable auto-generation if needed
- [ ] Test integration

### 6. Add Code Generation Tests (High Priority)
- [ ] Test type generation from various SQL schemas
- [ ] Test CRUD method generation
- [ ] Test nullable vs non-nullable handling
- [ ] Test primary key auto-increment detection
- [ ] Test foreign key extraction
- [ ] Integration test: generate code, compile, run queries
- [ ] Test project file generation

### 7. Handle Complex Scenarios (Low Priority)
- [ ] Composite primary keys (currently skipped)
- [ ] Many-to-many relationships via bridge tables
- [ ] Views (should they generate read-only types?)
- [ ] Custom query generation

## 🐛 Known Issues

1. **Composite Primary Keys Not Supported**
   - `generateGet` returns None for tables with multi-column PKs
   - Need to implement later

2. **FParsec Parser Incomplete**
   - Has compilation errors
   - Currently commented out
   - Regex parser is active

3. **Limited CRUD Methods**
   - Only Insert and GetById implemented
   - Need Update, Delete, GetAll

4. **No JOIN Generation**
   - Foreign keys detected but not used for query generation
   - Planned for future

5. **No Transaction Helpers**
   - Planned for future

## 📁 File Structure

```
src/
├── MigLib/
│   ├── CodeGen/
│   │   ├── CodeGen.fs              (Main orchestration)
│   │   ├── FabulousAstHelpers.fs   (Placeholder)
│   │   ├── FileMapper.fs           (SQL → F# mapping)
│   │   ├── ProjectGenerator.fs     (.fsproj generation)
│   │   ├── QueryGenerator.fs       (CRUD method generation)
│   │   └── TypeGenerator.fs        (Record type generation)
│   ├── DeclarativeMigrations/
│   │   ├── FParsecSqlParser.fs     (❌ Commented out - WIP)
│   │   ├── SqlParser.fs            (✅ Working regex parser)
│   │   └── SqlParser.fs.backup     (Backup)
│   └── MigLib.fsproj
├── mig/
│   └── Program.fs                  (Added codegen command)
└── Test/
    ├── TableMigration.fs           (✅ Passing)
    ├── ViewMigration.fs            (✅ Passing)
    └── UseAsLib.fs                 (✅ Passing)
```

## 🔧 Technical Details

### Type Mapping Logic
```fsharp
let mapSqlType (sqlType: SqlType) (isNullable: bool) : string =
  let baseType =
    match sqlType with
    | SqlInteger -> "int64"
    | SqlText -> "string"
    | SqlReal -> "float"
    | SqlTimestamp -> "DateTime"
    | SqlString -> "string"
    | SqlFlexible -> "obj"
  if isNullable then $"{baseType} option" else baseType
```

### Nullable Detection
```fsharp
let isColumnNullable (column: ColumnDef) : bool =
  column.constraints
  |> List.exists (fun c ->
    match c with
    | NotNull -> true
    | _ -> false)
  |> not  // Inverted: if NOT NULL exists, not nullable
```

### Primary Key Detection
```fsharp
let getPrimaryKey (table: CreateTable) : ColumnDef list =
  table.columns
  |> List.filter (fun col ->
    col.constraints
    |> List.exists (fun c ->
      match c with
      | PrimaryKey _ -> true
      | _ -> false))
```

## 🧪 Testing

### Current Test Status
All 3 tests passing with regex parser:
- ✅ TableMigration (6 cases)
- ✅ ViewMigration
- ✅ UseAsLib

### Manual Testing
```bash
# Create test SQL
mkdir -p /tmp/test_codegen
cd /tmp/test_codegen
cat > students.sql << 'EOF'
CREATE TABLE students (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    email TEXT,
    enrollment_date TIMESTAMP NOT NULL
);
EOF

# Run code generation
dotnet /path/to/mig/bin/Debug/net10.0/migrate.dll codegen

# Check output
ls -la
# Should see: students.sql, Students.fs, test_codegen.fsproj
```

### Generated Output Example
```fsharp
module Students

open System
open Microsoft.Data.Sqlite
open FsToolkit.ErrorHandling

type Students = {
    Id: int64 option
    Name: string
    Email: string option
    Enrollment_date: DateTime
}

type Students with
    static member Insert(conn: SqliteConnection, item: Students) : Result<int64, SqliteException> =
        try
            use cmd = new SqliteCommand("INSERT INTO students (name, email, enrollment_date) VALUES (@name, @email, @enrollment_date)", conn)
            cmd.Parameters.AddWithValue("@name", item.Name) |> ignore
            cmd.Parameters.AddWithValue("@email", match item.Email with Some v -> box v | None -> box DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("@enrollment_date", item.Enrollment_date) |> ignore
            cmd.ExecuteNonQuery() |> ignore
            Ok(conn.LastInsertRowId)
        with
        | :? SqliteException as ex -> Error ex

    static member GetById(conn: SqliteConnection, id: int64) : Result<Students option, SqliteException> =
        try
            use cmd = new SqliteCommand("SELECT id, name, email, enrollment_date FROM students WHERE id = @id", conn)
            cmd.Parameters.AddWithValue("@id", id) |> ignore
            use reader = cmd.ExecuteReader()
            if reader.Read() then
                Ok(Some {
                    Id = if reader.IsDBNull(0) then None else Some(reader.GetInt64(0))
                    Name = reader.GetString(1)
                    Email = if reader.IsDBNull(2) then None else Some(reader.GetString(2))
                    Enrollment_date = reader.GetDateTime(3)
                })
            else
                Ok None
        with
        | :? SqliteException as ex -> Error ex
```

## 📝 Git History

```
acd9847 Add F# code generation feature and remove Goose import support
ff92a05 Fix SQL parser to handle multiline CREATE TABLE statements
4f05659 Work in progress: FParsec-based SQL parser
```

## 💡 Design Decisions Made

1. **String Templates over Fabulous.AST** - Simpler, more direct for now
2. **Static Methods on Types** - More F# idiomatic than repository pattern
3. **Result Types** - Explicit error handling, no exceptions thrown
4. **File Colocation** - SQL and F# files together for discoverability
5. **Raw ADO.NET** - No ORM overhead, full control, minimal dependencies
6. **Option Types for Nullables** - Type-safe null handling

## 🔗 Related Files

- `spec.md` - Full specification with F# code generation details
- `CLAUDE.md` - F# coding conventions
- `README.md` - User-facing documentation

## 📞 Contact Points

When resuming:
1. Start with completing FParsec parser if prioritizing parser robustness
2. Or proceed with adding more CRUD methods if prioritizing functionality
3. Check current test status: `cd src && dotnet test`
4. Check current build: `cd src && dotnet build`
5. Test codegen: `cd /tmp && mkdir test && cd test && echo "CREATE TABLE test(id INTEGER PRIMARY KEY);" > test.sql && dotnet /path/to/mig codegen`
