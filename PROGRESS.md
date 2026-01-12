# F# Code Generation Implementation Progress

**Branch:** `fsharp-generation`
**Last Updated:** 2026-01-12
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
4. **`GetOne (tx)`** - Get first record using LIMIT 1
5. **`Update (item) (tx)`** - Update record
6. **`Delete (pkParams...) (tx)`** - Delete record

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
Views are read-only, so only `GetAll` and `GetOne` methods are generated (no Insert/Update/Delete). Column information is extracted using SQLite introspection by:
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

**New Tests:** `src/Test/ViewCodeGenTest.fs` (5 tests)
- View type generation includes columns
- View GetAll method is generated
- View with nullable columns generates option types
- Complex view with JOIN is supported
- View GetOne method is generated

### 12. GetOne Method Generation

**Files:**
- `src/MigLib/CodeGen/QueryGenerator.fs` - Added `generateGetOne` and `generateViewGetOne` functions
- Updated `generateTableCode` and `generateViewCode` to include GetOne methods

**Design Decision:**
GetOne provides a convenient way to fetch the first record from a table or view using `LIMIT 1`. This is useful for scenarios where you only need one record and don't care which one (e.g., checking if any records exist, getting a sample record).

**Benefits:**
- Simpler than GetAll when you only need one record
- More efficient than GetAll (stops after first record)
- Returns `Result<T option, SqliteException>` for consistent null handling
- Works with both tables and views

**Generated Code for Tables:**
```fsharp
static member GetOne (tx: SqliteTransaction) : Result<Student option, SqliteException> =
  try
    use cmd = new SqliteCommand("SELECT id, name, age FROM student LIMIT 1", tx.Connection, tx)
    use reader = cmd.ExecuteReader()
    if reader.Read() then
      Ok(Some { Id = reader.GetInt64(0); Name = reader.GetString(1); Age = reader.GetInt64(2) })
    else
      Ok None
  with
  | :? SqliteException as ex -> Error ex
```

**Generated Code for Views:**
```fsharp
static member GetOne (tx: SqliteTransaction) : Result<AdultStudents option, SqliteException> =
  try
    use cmd = new SqliteCommand("SELECT id, name FROM adult_students LIMIT 1", tx.Connection, tx)
    use reader = cmd.ExecuteReader()
    if reader.Read() then
      Ok(Some { Id = reader.GetInt64(0); Name = reader.GetString(1) })
    else
      Ok None
  with
  | :? SqliteException as ex -> Error ex
```

**Usage Example:**
```fsharp
Db.txn conn {
  let! maybeStudent = Student.GetOne
  match maybeStudent with
  | Some student -> printfn "Found: %s" student.Name
  | None -> printfn "No students found"
  return ()
}
```

**New Tests:**
- `src/Test/CompositePKTest.fs` - GetOne method is generated for tables
- `src/Test/ViewCodeGenTest.fs` - View GetOne method is generated

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
    ├── CompositePKTest.fs          (✅ Passing - 5 tests)
    ├── TransactionTest.fs          (✅ Passing - 6 tests)
    └── ViewCodeGenTest.fs          (✅ Passing - 5 tests)
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
All 19 tests passing:
- ✅ TableMigration (6 cases)
- ✅ ViewMigration
- ✅ UseAsLib
- ✅ CompositePKTest (5 tests for composite primary key support and GetOne)
- ✅ TransactionTest (6 tests for curried signatures and Db module)
- ✅ ViewCodeGenTest (5 tests for view code generation including GetOne)

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
   - ✅ All CRUD methods implemented (Insert, GetById, GetAll, GetOne, Update, Delete)
   - ✅ Composite primary key support (complete - both column-level and table-level)
   - ✅ Transaction support with Db module (curried signatures + computation expression)
   - ✅ Db module (shared transaction management in MigLib)
   - ✅ View code generation (read-only GetAll and GetOne methods with SQLite introspection)
   - ⏳ JOIN query generation (planned)
   - ⏳ Code generation tests (partial - composite PK, transaction, view, and GetOne tests added)
   - ⏳ Integration with `mig commit` command (not yet implemented)

3. Next priorities (in order):
   - Integrate code generation into `mig commit` command
   - Add JOIN query generation for foreign key relationships
   - Write more comprehensive code generation tests

4. Testing:
   - Check migration tests: `cd src && dotnet test` (should show all 19 passing)
   - Check build: `cd src && dotnet build`
   - Manual codegen test with views: `mkdir /tmp/test && cd /tmp/test && echo "CREATE TABLE test(id INTEGER PRIMARY KEY); CREATE VIEW test_view AS SELECT * FROM test;" > test.sql && dotnet /path/to/mig codegen`

## 🔄 Planned Feature: Normalized Schema Representation with Discriminated Unions

**Status:** ⏳ Planning Phase - Specification Complete, Implementation Not Started

**Goal:** Generate F# discriminated unions for normalized database schemas (2NF) that eliminate NULLs through table splitting, instead of using option types for nullable columns.

### Feature Overview

For schemas where optional data is represented by separate extension tables (1:1 relationship), generate discriminated unions that leverage F#'s type system for exhaustive pattern matching and domain modeling.

**Example:**
```sql
CREATE TABLE student (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL
);

CREATE TABLE student_address (
  student_id INTEGER PRIMARY KEY REFERENCES student(id),
  address TEXT NOT NULL
);
```

**Generates:**
```fsharp
[<RequireQualifiedAccess>]
type NewStudent =
  | Base of {| Name: string |}
  | WithAddress of {| Name: string; Address: string |}

[<RequireQualifiedAccess>]
type Student =
  | Base of {| Id: int64; Name: string |}
  | WithAddress of {| Id: int64; Name: string; Address: string |}
```

### Implementation Plan

#### Phase 1: Detection and Validation (Foundation)

**Goal:** Detect extension tables and validate schema constraints

**Tasks:**
1. ✅ **Specification Complete** - Documented in spec.md section 8
2. ⬜ **Create `NormalizedSchemaDetector.fs`** module in `src/MigLib/CodeGen/`
   - Function: `detectExtensionTables : CreateTable list -> (CreateTable * CreateTable list) list`
   - Input: List of all tables from schema
   - Output: List of (base table, extension tables) pairs

3. ⬜ **Implement Detection Algorithm**
   ```fsharp
   let detectExtensionTables (tables: CreateTable list) =
     tables
     |> List.choose (fun baseTable ->
       let extensions =
         tables
         |> List.filter (fun t ->
           // Check naming convention: {base}_{aspect}
           t.name.StartsWith $"{baseTable.name}_" &&
           // Check 1:1 FK relationship
           hasOneToOneForeignKey t baseTable)
       if extensions.IsEmpty then None
       else Some (baseTable, extensions))
   ```

4. ⬜ **Implement Validation Functions**
   - `hasOneToOneForeignKey : CreateTable -> CreateTable -> bool`
     - Verify FK column is also PK in extension table
   - `hasNullableColumns : CreateTable -> bool`
     - Check if any column lacks NOT NULL constraint
   - `validateNormalizedSchema : CreateTable -> CreateTable list -> Result<unit, string>`
     - Validate all tables in normalized group have no NULLs

5. ⬜ **Add Tests** in `src/Test/NormalizedSchemaDetectionTest.fs`
   - Test detection of single extension table
   - Test detection of multiple extension tables
   - Test rejection of tables with nullable columns
   - Test rejection of invalid FK relationships
   - Test naming convention matching

**Acceptance Criteria:**
- Correctly identifies base + extension table pairs
- Rejects schemas with nullable columns
- Validates 1:1 FK relationships
- All detection tests pass

#### Phase 2: Type Generation (Core Feature)

**Goal:** Generate discriminated unions with anonymous records

**Tasks:**
1. ⬜ **Create `NormalizedTypeGenerator.fs`** module
   - Function: `generateNormalizedTypes : CreateTable -> CreateTable list -> string * string`
   - Output: (NewType code, Type code) as strings

2. ⬜ **Implement Union Type Generation**
   ```fsharp
   let generateNormalizedTypes (baseTable: CreateTable) (extensions: CreateTable list) =
     let typeName = capitalize baseTable.name

     // Generate NewT type (for insert)
     let newType =
       generateDiscriminatedUnion $"New{typeName}" baseTable extensions false

     // Generate T type (for query)
     let queryType =
       generateDiscriminatedUnion typeName baseTable extensions true

     (newType, queryType)
   ```

3. ⬜ **Implement Case Generation**
   - `generateBaseCase : CreateTable -> bool -> string`
     - Generate `Base of {| fields |}`
     - Include ID if includeId=true (for query type)
   - `generateExtensionCase : CreateTable -> CreateTable list -> bool -> string`
     - Generate `With{Aspect} of {| base fields + extension fields |}`
     - Parse aspect name from table name suffix

4. ⬜ **Implement Anonymous Record Field Generation**
   - Reuse existing `TypeGenerator.mapSqlType` for field types
   - Generate field list with proper PascalCase naming
   - Format as anonymous record syntax

5. ⬜ **Add `[<RequireQualifiedAccess>]` Attribute**
   - Prepend attribute to all generated DU types

6. ⬜ **Update `CodeGen.fs`** to integrate normalized type generation
   - Check if table has extensions
   - If yes: use `NormalizedTypeGenerator.generateNormalizedTypes`
   - If no: use existing `TypeGenerator.generateRecordType`

7. ⬜ **Add Tests** in `src/Test/NormalizedTypeGenTest.fs`
   - Test single extension type generation
   - Test multiple extensions type generation
   - Test field name PascalCase conversion
   - Test ID inclusion/exclusion (New* vs *)
   - Test anonymous record syntax

**Acceptance Criteria:**
- Generates two DU types (New* and *)
- Base case and extension cases generated correctly
- Anonymous records have correct field names and types
- RequireQualifiedAccess attribute present
- All type generation tests pass

#### Phase 3: Query Generation - Insert (Critical Path)

**Goal:** Generate Insert method with pattern matching and multi-table inserts

**Tasks:**
1. ⬜ **Create `NormalizedQueryGenerator.fs`** module
   - Function: `generateNormalizedInsert : CreateTable -> CreateTable list -> string`

2. ⬜ **Implement Insert Method Skeleton**
   ```fsharp
   let generateNormalizedInsert (baseTable: CreateTable) (extensions: CreateTable list) =
     let typeName = capitalize baseTable.name
     $"""  static member Insert (item: New{typeName}) (tx: SqliteTransaction)
       : Result<int64, SqliteException> =
       try
         match item with
   {generateInsertCases baseTable extensions}
       with
       | :? SqliteException as ex -> Error ex"""
   ```

3. ⬜ **Implement Base Case Insert**
   - Single INSERT into base table
   - Return last_insert_rowid()

4. ⬜ **Implement Extension Case Insert**
   - Two INSERTs in same transaction:
     1. INSERT into base table, get ID
     2. INSERT into extension table with FK=ID
   - Atomic transaction (both succeed or both fail)

5. ⬜ **Handle Multiple Extensions**
   - Generate one case per extension
   - Each case does: base INSERT + extension INSERT

6. ⬜ **Add Tests** in `src/Test/NormalizedInsertTest.fs`
   - Test base case insert (no extension)
   - Test extension case insert (multi-table)
   - Test transaction atomicity (rollback on failure)
   - Test correct ID returned

**Acceptance Criteria:**
- Insert method compiles
- Pattern matching on NewT union
- Multi-table inserts are atomic
- Correct ID returned for all cases
- All insert tests pass

#### Phase 4: Query Generation - GetAll/GetById (Read Operations)

**Goal:** Generate query methods with LEFT JOINs and union case selection

**Tasks:**
1. ⬜ **Implement GetAll with LEFT JOIN**
   ```sql
   SELECT base.*, ext1.*, ext2.*
   FROM base
   LEFT JOIN ext1 ON base.id = ext1.base_id
   LEFT JOIN ext2 ON base.id = ext2.base_id
   ```

2. ⬜ **Implement Union Case Selection Logic**
   - Check which extension columns are NOT NULL
   - Map to appropriate union case:
     - All NULL → Base case
     - ext1 NOT NULL → WithExt1 case
     - ext2 NOT NULL → WithExt2 case

3. ⬜ **Handle Multiple Extensions**
   - If multiple extensions present for same row: choose first (limitation)
   - Log warning or return Base case

4. ⬜ **Implement GetById**
   - Similar to GetAll but with WHERE clause
   - Returns `Result<T option, SqliteException>`

5. ⬜ **Implement GetOne**
   - Add LIMIT 1 to GetAll query

6. ⬜ **Add Tests** in `src/Test/NormalizedQueryTest.fs`
   - Test GetAll with mixed cases
   - Test GetById returns correct case
   - Test LEFT JOIN includes all records
   - Test case selection logic

**Acceptance Criteria:**
- GetAll returns correct union cases
- LEFT JOINs work correctly
- Case selection handles all scenarios
- GetById and GetOne work
- All query tests pass

#### Phase 5: Query Generation - Update/Delete (Write Operations)

**Goal:** Generate Update and Delete methods with pattern matching

**Tasks:**
1. ⬜ **Implement Update Method**
   - Pattern match on T union:
     - Base case: UPDATE base, DELETE extensions
     - Extension case: UPDATE base, INSERT OR REPLACE extension

2. ⬜ **Implement Delete Method**
   - Simple DELETE from base table
   - Extensions cascade via FK constraint

3. ⬜ **Add Tests** in `src/Test/NormalizedUpdateDeleteTest.fs`
   - Test update from Base to WithExtension
   - Test update from WithExtension to Base
   - Test delete cascades to extensions

**Acceptance Criteria:**
- Update transitions between cases correctly
- Delete cascades properly
- All update/delete tests pass

#### Phase 6: Error Handling and Validation

**Goal:** Provide clear error messages for invalid schemas

**Tasks:**
1. ⬜ **Implement Validation Error Types**
   ```fsharp
   type NormalizedSchemaError =
     | NullableColumnsDetected of table: string * columns: string list
     | InvalidForeignKey of extension: string * base: string
     | InvalidNaming of table: string * expected: string
     | MultipleExtensionsActive of table: string
   ```

2. ⬜ **Implement Error Reporting**
   - Detect schema violations during generation
   - Return meaningful error messages
   - Suggest fixes (e.g., "Add NOT NULL to column X")

3. ⬜ **Add Validation Tests**
   - Test error on nullable columns
   - Test error on invalid FK
   - Test error on naming mismatch

**Acceptance Criteria:**
- Clear error messages for all validation failures
- Users know how to fix schema issues
- All validation tests pass

#### Phase 7: Integration and Documentation

**Goal:** Integrate with existing code generation pipeline

**Tasks:**
1. ⬜ **Update `CodeGen.fs`** Main Flow
   - Add normalized schema detection step
   - Branch to normalized vs regular generation
   - Handle mixed schemas (some normalized, some not)

2. ⬜ **Update CLI**
   - Show normalized table count in codegen output
   - Add flag to disable normalized generation: `--no-normalized`

3. ⬜ **Write Documentation**
   - Update CodeGen/README.md with normalized schema examples
   - Add migration guide (option types → discriminated unions)
   - Add troubleshooting section

4. ⬜ **Add End-to-End Tests**
   - Full workflow test: SQL → codegen → compile → execute
   - Test with real-world normalized schema example

5. ⬜ **Update PROGRESS.md**
   - Mark feature as complete
   - Document any limitations discovered

**Acceptance Criteria:**
- Feature integrated into main pipeline
- Documentation complete and accurate
- End-to-end tests pass
- Feature ready for use

### Estimated Implementation Order

1. **Week 1**: Phase 1 (Detection) + Phase 2 (Type Gen) - Foundation
2. **Week 2**: Phase 3 (Insert) + Phase 4 (Queries) - Core functionality
3. **Week 3**: Phase 5 (Update/Delete) + Phase 6 (Errors) - Complete CRUD
4. **Week 4**: Phase 7 (Integration) + Testing + Documentation - Polish

### Testing Strategy

**Unit Tests:**
- Detection algorithm (5 tests)
- Type generation (7 tests)
- Insert generation (4 tests)
- Query generation (6 tests)
- Update/Delete generation (3 tests)
- Validation (4 tests)
- **Total: ~30 unit tests**

**Integration Tests:**
- End-to-end: SQL schema → generated code → compilation → execution
- Mixed schemas (normalized + regular tables)
- Multiple extensions per base table

**Manual Testing:**
- Real-world schema examples
- Performance with large datasets
- Generated code quality review

### Known Limitations

1. **No Combinatorial Cases**: Multiple extensions create separate cases, not combinations
   - `Base | WithAddress | WithEmail` (3 cases)
   - NOT `Base | WithAddress | WithEmail | WithAddressEmail` (4 cases)

2. **One Active Extension**: If a row has multiple extensions, only one is loaded
   - Limitation of current design
   - Could be addressed in future with combinatorial generation

3. **Naming Convention Required**: Extension tables MUST follow `{base}_{aspect}` pattern
   - No flexibility in naming
   - Clear error if pattern not followed

4. **Manual Migration**: Converting existing option-based code to DU-based requires manual work
   - No automated migration tool
   - Documentation provides migration guide

### Success Metrics

- ✅ All 30+ unit tests passing
- ✅ End-to-end integration test passing
- ✅ Generated code compiles without warnings
- ✅ Documentation complete with examples
- ✅ Real-world schema tested successfully
- ✅ Performance acceptable (< 100ms for typical schema)

### Dependencies

- Current code generation pipeline (working)
- FParsec SQL parser (working)
- Db module with txn CE (working)
- Test infrastructure (working)

### Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Complex query generation | High | Start with simple cases, iterate |
| Anonymous record syntax issues | Medium | Extensive testing, fallback to named types |
| Performance with many extensions | Low | Profile and optimize if needed |
| User confusion with two DU types | Medium | Clear documentation and examples |

### Future Enhancements (Post-MVP)

1. **Combinatorial Cases**: Generate all combinations of extensions
2. **Flexible Naming**: Support custom naming patterns via config
3. **Automated Migration**: Tool to convert option-based to DU-based
4. **View Support**: Generate DUs for views with LEFT JOINs
5. **Lazy Loading**: Load extensions on-demand rather than eagerly
