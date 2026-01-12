# F# Code Generation Implementation Progress

**Branch:** `fsharp-generation`
**Last Updated:** 2026-01-11
**Status:** ✅ FParsec parser complete and active, code generation working, all tests passing

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

### 3. Created Db Module for Transaction Management
Created `src/MigLib/Db.fs` - Shared transaction management utilities used by all generated code:

#### **Db.fs**
- `WithTransaction(conn, action)`: Function for explicit transaction management with automatic commit/rollback
- `TxnBuilder`: Computation expression builder that combines transactions with Result monad
- `txn conn { ... }`: Computation expression for clean transaction syntax
- Packaged with MigLib 2.0 for use by generated code

**Benefits:**
- Single implementation (no duplication in generated code)
- Clean computation expression syntax
- Supports partial application patterns
- Explicit error handling with Result types

### 4. Created CodeGen Module Structure
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
- `getPrimaryKey`: Extracts primary key column(s) from table (supports both column-level and table-level PKs)
- `getForeignKeys`: Extracts foreign key relationships
- `capitalize`: Helper for F# naming conventions
- `generateInsert`: Generates INSERT method with curried signature `(item) (tx)`
  - Excludes auto-increment primary keys
  - Returns `last_insert_rowid`
  - Uses `tx.Connection` internally
- `generateGet`: Generates GetById method with curried signature `(pkParams...) (tx)`
  - Supports single and composite primary keys
  - Handles nullable column reads with `IsDBNull` checks
- `generateGetAll`: Generates GetAll method with curried signature `(tx)`
- `generateUpdate`: Generates Update method with curried signature `(item) (tx)` (composite PK support)
- `generateDelete`: Generates Delete method with curried signature `(pkParams...) (tx)` (composite PK support)
- `generateTableCode`: Orchestrates generation of all methods for a table

**Note:** All CRUD methods use curried signatures with `SqliteTransaction` as last parameter for clean computation expression syntax.

**Generated Method Pattern:**
```fsharp
// Db module (part of MigLib 2.0, shared across all generated code)
module Db =
  let WithTransaction (conn: SqliteConnection) (action: SqliteTransaction -> Result<'T, SqliteException>) =
    let tx = conn.BeginTransaction()
    try
      match action tx with
      | Ok result -> tx.Commit(); Ok result
      | Error ex -> tx.Rollback(); Error ex
    with :? SqliteException as ex -> tx.Rollback(); Error ex

  type TxnBuilder(conn: SqliteConnection) =
    member _.Bind(m, f) = fun (tx: SqliteTransaction) -> match m tx with | Ok v -> f v tx | Error ex -> Error ex
    member _.Return(x) = fun _ -> Ok x
    member _.ReturnFrom(m) = m
    member _.Run(action) = WithTransaction conn action

  let txn (conn: SqliteConnection) = TxnBuilder(conn)

// Generated code (uses curried signatures)
type Student with
  static member Insert (item: Student) (tx: SqliteTransaction) : Result<int64, SqliteException> =
    try
      use cmd = new SqliteCommand("INSERT INTO students (...) VALUES (...)", tx.Connection, tx)
      cmd.Parameters.AddWithValue("@name", item.Name) |> ignore
      cmd.ExecuteNonQuery() |> ignore
      use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
      let lastId = lastIdCmd.ExecuteScalar() |> unbox<int64>
      Ok lastId
    with :? SqliteException as ex -> Error ex

  static member GetById (id: int64) (tx: SqliteTransaction) : Result<Student option, SqliteException> =
    try
      use cmd = new SqliteCommand("SELECT ... FROM students WHERE id = @id", tx.Connection, tx)
      cmd.Parameters.AddWithValue("@id", id) |> ignore
      use reader = cmd.ExecuteReader()
      if reader.Read() then Ok(Some { Id = reader.GetInt64(0); ... })
      else Ok None
    with :? SqliteException as ex -> Error ex
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
  - Package references: FSharp.Core 10.0.100, FsToolkit.ErrorHandling 4.18.0, Microsoft.Data.Sqlite 9.0.0
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
open migrate.Db

type Students = {
    Id: int64 option
    Name: string
    Email: string option
    Enrollment_date: DateTime
}

type Students with
    [Insert, GetById, GetAll, Update, Delete methods with curried signatures...]
```

### 5. Added CLI Command
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

### 6. Fixed SQL Parser
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

### 7. Updated Documentation
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

### 8. Completed FParsec-based SQL Parser

**File:** `src/MigLib/DeclarativeMigrations/FParsecSqlParser.fs` (now active)

**Goal:** Replace regex-based parser with robust FParsec parser combinator implementation ✅

**Status:** COMPLETE - All tests passing, parser in active use

**Fixes Applied:**
1. **Fixed parse {} type errors**: Added `>>% ()` to all `do!` expressions that return values
   - Column constraint parsers: `defaultValue`, `check`, `foreignKey`
   - Table constraint parsers: `primaryKey`, `foreignKey`, `unique`

2. **Fixed function naming conflict**: Renamed `parse` to `parseSqlFile` to avoid conflict with FParsec's `parse` computation expression

3. **Fixed Result constructor issue**: Used fully qualified `Result.Ok` and `Result.Error`

4. **Made parser case-insensitive**: Changed `pstring` to `pstringCI` in all string parsers
   - Allows both "INTEGER" and "integer", "TEXT" and "text", etc.

5. **Fixed statement backtracking**: Added `attempt` to all statement parsers in `choice` to allow proper backtracking when parsing multiple statements

6. **Fixed SQL type whitespace handling**: Changed `str_ws1` to `str_ws` in `typeParser` to allow no whitespace before comma

**Integration:**
- Updated `SqlParser.fs` to use `FParsecSqlParser.parseSqlFile` instead of regex parsing
- Kept view post-processing logic (`prostProcViews`) for dependency extraction
- Removed all old regex-based parsing code
- Removed debug test file and backup file

**Test Results:**
- ✅ All 12 tests pass (TableMigration, ViewMigration, UseAsLib, CompositePKTest x4, TransactionTest x5)
- ✅ Manual testing with real SQL files successful
- ✅ Code generation works with FParsec parser

### 9. Composite Primary Key Support

**File:** `src/MigLib/CodeGen/QueryGenerator.fs`

**Changes:**
1. **Updated `getPrimaryKey`** - Now checks both column-level and table-level constraints
   - Table-level `PRIMARY KEY(col1, col2)` syntax properly recognized
   - Returns all columns that are part of the primary key

2. **Updated `generateGet`** - Supports composite PKs
   - Generates multiple parameters: `GetById(conn, student_id: int64, course_id: int64)`
   - WHERE clause uses `AND` for all PK columns

3. **Updated `generateUpdate`** - Supports composite PKs
   - Excludes all PK columns from SET clause
   - WHERE clause uses all PK columns

4. **Updated `generateDelete`** - Supports composite PKs
   - Generates multiple parameters for all key columns
   - WHERE clause matches all PK columns

**New Tests:** `src/Test/CompositePKTest.fs`
- Composite PK parsing and recognition
- GetById method generation with composite PK
- Delete method generation with composite PK
- Update method exclusion of all PK columns from SET

### 10. Transaction-Only API with Curried Signatures

**Files:**
- `src/MigLib/Db.fs` - Shared transaction management module
- `src/MigLib/CodeGen/QueryGenerator.fs` - Code generation with curried signatures

**Design Decision:**
- Transaction management moved to shared `Db` module (no longer generated per-type)
- All CRUD methods use curried signatures with `SqliteTransaction` as last parameter
- This enables clean computation expression syntax and partial application

**Benefits:**
- Clean syntax: `Db.txn conn { let! id = Student.Insert student }` (no tx parameter needed)
- Partial application: `let insertFunc = Student.Insert student` (applies tx later)
- Enforces transactional thinking and atomic operations
- Consistent API across all methods
- Reduces generated code duplication

**Db Module (in MigLib 2.0):**
1. **`Db.WithTransaction(conn, action)`** - Explicit transaction function
2. **`Db.txn conn { ... }`** - Computation expression for clean syntax

**Generated Methods (curried signatures):**
1. **`Insert (item) (tx)`** - Insert using `tx.Connection` internally
2. **`GetById (pkParams...) (tx)`** - Get by primary key
3. **`GetAll (tx)`** - Get all records
4. **`Update (item) (tx)`** - Update record
5. **`Delete (pkParams...) (tx)`** - Delete record

**Usage Examples:**
```fsharp
// Option 1: Db.txn computation expression (recommended)
Db.txn conn {
  let! id1 = Student.Insert student1
  let! id2 = Student.Insert student2
  let! all = Student.GetAll
  return (id1, id2, all)
}

// Option 2: Explicit Db.WithTransaction
Db.WithTransaction conn (fun tx ->
  result {
    let! id = Student.Insert student tx
    return id
  })

// Option 3: Partial application
let insertStudent = Student.Insert student
Db.txn conn {
  let! id = insertStudent
  return id
}
```

**New Tests:** `src/Test/TransactionTest.fs` (7 tests)
- Insert method uses curried signature with tx last
- GetById method uses curried signature with tx last
- GetAll method uses curried signature
- Update method uses curried signature with tx last
- Delete method uses curried signature with tx last
- Generated table code uses curried signatures for all methods
- WithTransaction is NOT generated on types (verified)

### 11. View Code Generation Support

**Files:**
- `src/MigLib/CodeGen/ViewIntrospection.fs` - SQLite introspection for view columns
- `src/MigLib/CodeGen/TypeGenerator.fs` - Added `generateViewRecordType` function
- `src/MigLib/CodeGen/QueryGenerator.fs` - Added `generateViewCode` and `generateViewGetAll` functions
- `src/MigLib/CodeGen/CodeGen.fs` - Updated to process views alongside tables

**Design Decision:**
Views are read-only, so only `GetAll` method is generated (no Insert/Update/Delete). Column information is extracted using SQLite introspection by:
1. Creating temporary in-memory database
2. Creating all tables (views depend on them)
3. Creating the view
4. Using `PRAGMA table_info(view_name)` to extract columns

**Benefits:**
- Leverages SQLite's own type inference for views
- No need to parse complex SELECT statements
- Works with any valid SQL view including JOINs, CTEs, UNION, etc.
- Consistent API with table types

**Generated Code for Views:**
```fsharp
// View record type
type Adult_students = {
  Id: int64
  Name: string
}

// View query method (read-only)
type Adult_students with
  static member GetAll (tx: SqliteTransaction) : Result<Adult_students list, SqliteException> =
    try
      use cmd = new SqliteCommand("SELECT id, name FROM adult_students", tx.Connection, tx)
      use reader = cmd.ExecuteReader()
      let results = ResizeArray<Adult_students>()
      while reader.Read() do
        results.Add({ Id = reader.GetInt64(0); Name = reader.GetString(1) })
      Ok(results |> Seq.toList)
    with
    | :? SqliteException as ex -> Error ex
```

**Usage Example:**
```fsharp
Db.txn conn {
  let! adults = Adult_students.GetAll
  return adults
}
```

**New Tests:** `src/Test/ViewCodeGenTest.fs` (4 tests)
- View type generation includes columns
- View GetAll method is generated
- View with nullable columns generates option types
- Complex view with JOIN is supported

## ⏭️ Next Steps (Priority Order)

### 1. Add More CRUD Methods (High Priority) ✅ COMPLETED
- [x] Implement `Update` method
- [x] Implement `Delete` method
- [x] Implement `GetAll` method
- [ ] Test generated CRUD methods

### 2. Add JOIN Query Generation (Medium Priority)
- [ ] Detect foreign key relationships
- [ ] Generate methods like `GetStudentWithCourses`
- [ ] Handle one-to-many relationships
- [ ] Test JOIN queries

### 3. Add Transaction Support (Medium Priority) ✅ COMPLETED
- [x] Generate `WithTransaction` helper method
- [x] Add transaction-aware CRUD method overloads (Insert, Update, Delete)
- [x] Test transaction method generation

### 4. Update mig commit Integration (High Priority)
- [ ] Add call to `CodeGen.generateCode` after successful migration in `commit` command
- [ ] Handle errors gracefully
- [ ] Add flag to disable auto-generation if needed
- [ ] Test integration

### 5. Add Code Generation Tests (High Priority)
- [ ] Test type generation from various SQL schemas
- [ ] Test CRUD method generation
- [ ] Test nullable vs non-nullable handling
- [ ] Test primary key auto-increment detection
- [ ] Test foreign key extraction
- [ ] Integration test: generate code, compile, run queries
- [ ] Test project file generation

### 6. Handle Complex Scenarios (Low Priority)
- [x] Composite primary keys ✅ COMPLETED
- [ ] Many-to-many relationships via bridge tables
- [ ] Views (should they generate read-only types?)
- [ ] Custom query generation

## 🐛 Known Issues

1. **No JOIN Generation**
   - Foreign keys detected but not used for query generation
   - Planned for future

## 📁 File Structure

```
src/
├── MigLib/
│   ├── Db.fs                       (✅ Transaction management for generated code)
│   ├── CodeGen/
│   │   ├── CodeGen.fs              (Main orchestration, processes tables and views)
│   │   ├── FabulousAstHelpers.fs   (Placeholder)
│   │   ├── FileMapper.fs           (SQL → F# mapping)
│   │   ├── ProjectGenerator.fs     (.fsproj generation)
│   │   ├── QueryGenerator.fs       (CRUD method generation with curried signatures)
│   │   ├── TypeGenerator.fs        (Record type generation for tables and views)
│   │   └── ViewIntrospection.fs    (✅ SQLite introspection for view columns)
│   ├── DeclarativeMigrations/
│   │   ├── FParsecSqlParser.fs     (✅ Active FParsec parser)
│   │   └── SqlParser.fs            (✅ Uses FParsec parser, view post-processing)
│   └── MigLib.fsproj
├── mig/
│   └── Program.fs                  (Added codegen command)
└── Test/
    ├── TableMigration.fs           (✅ Passing)
    ├── ViewMigration.fs            (✅ Passing)
    ├── UseAsLib.fs                 (✅ Passing)
    ├── CompositePKTest.fs          (✅ Passing - 4 tests)
    ├── TransactionTest.fs          (✅ Passing - 6 tests)
    └── ViewCodeGenTest.fs          (✅ Passing - 4 tests)
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
  // First check for column-level primary keys
  let columnLevelPks =
    table.columns
    |> List.filter (fun col ->
      col.constraints
      |> List.exists (fun c ->
        match c with
        | PrimaryKey _ -> true
        | _ -> false))

  // Then check for table-level primary keys (composite PKs)
  let tableLevelPkCols =
    table.constraints
    |> List.tryPick (fun c ->
      match c with
      | PrimaryKey pk when pk.columns.Length > 0 -> Some pk.columns
      | _ -> None)
    |> Option.defaultValue []
    |> List.choose (fun colName ->
      table.columns |> List.tryFind (fun col -> col.name = colName))

  // Prefer table-level if present, otherwise use column-level
  if tableLevelPkCols.Length > 0 then tableLevelPkCols else columnLevelPks
```

## 🧪 Testing

### Current Test Status
All 17 tests passing:
- ✅ TableMigration (6 cases)
- ✅ ViewMigration
- ✅ UseAsLib
- ✅ CompositePKTest (4 tests for composite primary key support)
- ✅ TransactionTest (6 tests for curried signatures and Db module)
- ✅ ViewCodeGenTest (4 tests for view code generation)

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
open migrate.Db

type Students = {
    Id: int64 option
    Name: string
    Email: string option
    Enrollment_date: DateTime
}

type Students with
  static member Insert (item: Students) (tx: SqliteTransaction) : Result<int64, SqliteException> =
    try
      use cmd = new SqliteCommand("INSERT INTO students (name, email, enrollment_date) VALUES (@name, @email, @enrollment_date)", tx.Connection, tx)
      cmd.Parameters.AddWithValue("@name", item.Name) |> ignore
      cmd.Parameters.AddWithValue("@email", match item.Email with Some v -> box v | None -> box DBNull.Value) |> ignore
      cmd.Parameters.AddWithValue("@enrollment_date", item.Enrollment_date) |> ignore
      cmd.ExecuteNonQuery() |> ignore
      use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
      let lastId = lastIdCmd.ExecuteScalar() |> unbox<int64>
      Ok lastId
    with :? SqliteException as ex -> Error ex

  static member GetById (id: int64) (tx: SqliteTransaction) : Result<Students option, SqliteException> =
    try
      use cmd = new SqliteCommand("SELECT id, name, email, enrollment_date FROM students WHERE id = @id", tx.Connection, tx)
      cmd.Parameters.AddWithValue("@id", id) |> ignore
      use reader = cmd.ExecuteReader()
      if reader.Read() then
        Ok(Some {
          Id = if reader.IsDBNull(0) then None else Some(reader.GetInt64(0))
          Name = reader.GetString(1)
          Email = if reader.IsDBNull(2) then None else Some(reader.GetString(2))
          Enrollment_date = reader.GetDateTime(3)
        })
      else Ok None
    with :? SqliteException as ex -> Error ex

  static member GetAll (tx: SqliteTransaction) : Result<Students list, SqliteException> =
    try
      use cmd = new SqliteCommand("SELECT id, name, email, enrollment_date FROM students", tx.Connection, tx)
      use reader = cmd.ExecuteReader()
      let results = ResizeArray<Students>()
      while reader.Read() do
        results.Add({
          Id = if reader.IsDBNull(0) then None else Some(reader.GetInt64(0))
          Name = reader.GetString(1)
          Email = if reader.IsDBNull(2) then None else Some(reader.GetString(2))
          Enrollment_date = reader.GetDateTime(3)
        })
      Ok(results |> Seq.toList)
    with :? SqliteException as ex -> Error ex

  static member Update (item: Students) (tx: SqliteTransaction) : Result<unit, SqliteException> =
    try
      use cmd = new SqliteCommand("UPDATE students SET name = @name, email = @email, enrollment_date = @enrollment_date WHERE id = @id", tx.Connection, tx)
      cmd.Parameters.AddWithValue("@id", match item.Id with Some v -> box v | None -> box DBNull.Value) |> ignore
      cmd.Parameters.AddWithValue("@name", item.Name) |> ignore
      cmd.Parameters.AddWithValue("@email", match item.Email with Some v -> box v | None -> box DBNull.Value) |> ignore
      cmd.Parameters.AddWithValue("@enrollment_date", item.Enrollment_date) |> ignore
      cmd.ExecuteNonQuery() |> ignore
      Ok()
    with :? SqliteException as ex -> Error ex

  static member Delete (id: int64) (tx: SqliteTransaction) : Result<unit, SqliteException> =
    try
      use cmd = new SqliteCommand("DELETE FROM students WHERE id = @id", tx.Connection, tx)
      cmd.Parameters.AddWithValue("@id", id) |> ignore
      cmd.ExecuteNonQuery() |> ignore
      Ok()
    with :? SqliteException as ex -> Error ex
```

**Usage with Db.txn:**
```fsharp
open migrate.Db

Db.txn conn {
  let! id = Students.Insert { Id = None; Name = "Alice"; Email = Some "alice@example.com"; Enrollment_date = DateTime.Now }
  let! student = Students.GetById id
  return student
}
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
7. **Shared Db Module** - Transaction management extracted to MigLib (not generated per-type)
   - Single implementation shared across all generated code
   - Reduces code duplication
   - Provides both `Db.WithTransaction` and `Db.txn` computation expression
8. **Curried Signatures with Transaction Last** - All CRUD methods use curried signatures
   - SqliteTransaction as last parameter enables clean computation expression syntax
   - Allows partial application patterns
   - Works seamlessly with `Db.txn` (transaction parameter automatically supplied)
   - Consistent API across all methods

## 🔗 Related Files

- `spec.md` - Full specification with F# code generation details
- `CLAUDE.md` - F# coding conventions
- `README.md` - User-facing documentation

## 📞 Contact Points

When resuming:
1. **FParsec parser is COMPLETE** ✅ - Robust SQL parsing with proper error recovery
2. **Current implementation status:**
   - ✅ SQL parsing with FParsec (complete, active)
   - ✅ Record type generation for tables and views (working)
   - ✅ All CRUD methods implemented (Insert, GetById, GetAll, Update, Delete)
   - ✅ Composite primary key support (complete - both column-level and table-level)
   - ✅ Transaction support with Db module (curried signatures + computation expression)
   - ✅ Db module (shared transaction management in MigLib)
   - ✅ View code generation (read-only GetAll method with SQLite introspection)
   - ⏳ JOIN query generation (planned)
   - ⏳ Code generation tests (partial - composite PK, transaction, and view tests added)
   - ⏳ Integration with `mig commit` command (not yet implemented)

3. Next priorities (in order):
   - Integrate code generation into `mig commit` command
   - Add JOIN query generation for foreign key relationships
   - Write more comprehensive code generation tests

4. Testing:
   - Check migration tests: `cd src && dotnet test` (should show all 17 passing)
   - Check build: `cd src && dotnet build`
   - Manual codegen test with views: `mkdir /tmp/test && cd /tmp/test && echo "CREATE TABLE test(id INTEGER PRIMARY KEY); CREATE VIEW test_view AS SELECT * FROM test;" > test.sql && dotnet /path/to/mig codegen`
