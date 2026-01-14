# Migrate - Database Migration Tool & F# Type Generator Specification

## Overview

Migrate is a declarative database migration tool and F# type generator for SQLite. It automatically generates and executes SQL statements to transform an actual database schema into an expected schema, and generates type-safe F# code for database access. Rather than requiring developers to write migration scripts or data access code manually, Migrate compares schemas, generates DDL statements, and produces F# types with CRUD operations.

**Version:** 2.0.0 (planned - major feature: F# code generation)
**Target Framework:** .NET 10.0
**Language:** F#
**Database:** SQLite
**License:** Apache 2.0

## Functional Type Relational Mapping (FTRM)

While Object-Relational Mapping (ORM) tools map database relations to object-oriented classes, Migrate implements **Functional Type Relational Mapping (FTRM)** - a paradigm that maps database relations to functional types.

### ORM vs FTRM Comparison

**Traditional ORM (Object-Relational Mapping):**
```csharp
// Relations → Objects
public class Student {
    public long Id { get; set; }
    public string Name { get; set; }
    public string? Address { get; set; }  // Nullable reference
}

// Optional data represented by null
var student = new Student { Id = 1, Name = "Alice", Address = null };
```

**FTRM (Functional Type Relational Mapping):**
```fsharp
// Relations → Functional Types
type Student = {
    Id: int64
    Name: string
    Address: string option  // Option type for nullable data
}

// Optional data represented by option type
let student = { Id = 1L; Name = "Alice"; Address = None }
```

### Why Functional Types?

Functional types provide several advantages for database access:

1. **Algebraic Data Types (ADTs)**: Discriminated unions naturally represent denormalized data and table relationships
2. **Immutability**: Generated types are immutable by default, preventing accidental mutations
3. **Type Safety**: Option types make nullability explicit and compiler-enforced
4. **Pattern Matching**: Exhaustive matching ensures all cases are handled
5. **Composition**: Functional types compose better through function pipelines
6. **Domain Modeling**: Sum types (DUs) model domain states explicitly (e.g., `Student.Base | Student.WithAddress`)

### FTRM in Practice

Migrate generates functional types that preserve database semantics:

- **Tables → Records**: Each table becomes an F# record type with properly typed fields
- **NULL → Option**: Nullable columns map to `option` types
- **Foreign Keys → Types**: Referenced tables become typed references (planned)
- **1:1 Extensions → Discriminated Unions**: Optional related data becomes DU cases instead of nullable fields
- **Transactions → Functions**: All operations are pure functions taking a transaction parameter

This functional approach provides **type-level guarantees** about data structure and relationships, catching errors at compile time rather than runtime.

## Core Features

### 1. Declarative Migrations
Instead of writing imperative migration scripts, users define the expected database schema in SQL files. Migrate automatically:
- Detects differences between actual (database) and expected (schema files) schemas
- Generates DDL statements to transform actual into expected
- Orders statements respecting dependencies
- Executes migrations in sequence with transaction safety

**Supported Elements:**
- Tables (CREATE, ALTER, DROP)
- Columns (ADD, DROP, type changes)
- Views (CREATE, DROP)
- Indexes (CREATE, DROP)
- Triggers (CREATE, DROP)
- Constraints (PRIMARY KEY, FOREIGN KEY, UNIQUE, NOT NULL, CHECK, DEFAULT)

### 2. SQL Parsing
**Implementation:** FParsec-based parser combinators (pure F# implementation, no external parser generators like ANTLR)

**Parser Capabilities:**
- Parses CREATE TABLE statements with column definitions and constraints
- Extracts table names, column names, data types, and constraints
- Handles quoted identifiers (backticks, double quotes, square brackets)
- Identifies foreign key relationships for dependency analysis
- Parses CREATE VIEW, CREATE INDEX, and CREATE TRIGGER statements
- Supports table-level and column-level constraints

**Data Types Supported:**
- INTEGER
- TEXT
- REAL
- TIMESTAMP
- STRING
- Flexible (when type not specified)

**Constraints Supported:**
- PRIMARY KEY (with AUTOINCREMENT support)
- FOREIGN KEY (with reference tracking)
- UNIQUE
- NOT NULL
- DEFAULT
- CHECK
- AUTOINCREMENT

### 3. Dependency Resolution and Topological Sorting
Migrate automatically resolves dependencies between database elements:

**Dependency Types:**
- Table-to-table dependencies via FOREIGN KEY constraints
- View dependencies on tables or other views
- Index dependencies on tables
- Trigger dependencies on tables or views

**Algorithm:** Topological sort ensures that:
- Tables with foreign keys are created/dropped in correct order
- Views depend on their referenced tables
- Circular dependencies are detected and reported

### 4. Column Migration Strategy
Column changes are handled with two approaches:

**Simple Changes (columns without constraints):**
- Direct ALTER TABLE ADD COLUMN / DROP COLUMN statements

**Complex Changes (columns with constraints):**
- Table recreation strategy:
  1. Create temporary table with new schema
  2. Copy data from original table
  3. Drop original table
  4. Rename temporary table
- Generates WARNING comments for non-default value assignments requiring manual intervention

### 5. Migration Execution
Provides both CLI and library interfaces for executing migrations:

**CLI Commands:**
- `mig init` - Initialize migration project with example files
- `mig status` - Generate migration SQL (dry-run)
- `mig commit` - Execute generated migration with auto-regeneration
- `mig log` - Display migration history and metadata
- `mig schema` - Show current database schema
- `mig codegen` - Generate F# types from SQL schema files
- `mig seed` - Execute seed statements (INSERT OR REPLACE) from SQL files

**Library Interface:**
- MigLib NuGet package for programmatic access
- Supports integration into C# or F# applications

### 6. Migration Logging
Tracks all executed migrations with:
- Migration timestamp
- SQL statements executed
- Success/failure status
- Metadata storage in `_schema_migration` table

### 7. F# Type Generation
Generates type-safe F# code from SQL schema definitions:

**Generated Code Structure:**
- One F# module per SQL file (e.g., `students.sql` → `Students.fs`)
- Record types for each table
- Static CRUD methods on types
- Automatic JOIN queries for foreign key relationships
- Transaction helper methods

**Code Generation Features:**
- **Type Mapping:** SQL types → F# types (INTEGER → int64, TEXT → string, etc.)
- **Null Handling:** NOT NULL columns → direct types, nullable columns → option types
- **CRUD Operations** (all use curried signatures with `SqliteTransaction` last):
  - `Insert (item) (tx)` - Returns `Result<int64, SqliteException>` with last_insert_rowid
  - `Update (item) (tx)` - Returns `Result<unit, SqliteException>`
  - `Delete (pkParams...) (tx)` - Returns `Result<unit, SqliteException>`
  - `GetById (pkParams...) (tx)` - Returns `Result<T option, SqliteException>`
  - `GetAll (tx)` - Returns `Result<T list, SqliteException>`
- **Transaction Management:**
  - `Db.WithTransaction(conn, action)` - Shared function for atomic operations with automatic commit/rollback
  - `Db.txn` - Computation expression combining transactions with Result monad for clean syntax
- **Foreign Key Queries:** Automatic generation of methods to query related entities via JOINs (planned)
- **Error Handling:** All operations return `Result<T, SqliteException>`

**Design Decision - Curried Signatures with Transaction Last:**
All CRUD methods use curried signatures with `SqliteTransaction` as the last parameter. This design:
- Enables clean computation expression syntax without mentioning the transaction parameter
- Allows partial application (e.g., `Student.Insert student` creates a function waiting for tx)
- Enforces transactional thinking and atomic operations
- Provides a consistent API (all methods follow same pattern)
- Works seamlessly with `Db.txn` computation expression
- Aligns with SQLite reality (without explicit transactions, each statement auto-commits anyway)

**Implementation:**
- Uses [Fabulous.AST](https://github.com/edgarfgp/Fabulous.AST) for type-safe F# code generation
- Uses raw ADO.NET (Microsoft.Data.Sqlite) for database operations
- Generated code colocated with SQL files in same directory

**Example Generated Code:**
```fsharp
module Students

open System
open Microsoft.Data.Sqlite
open FsToolkit.ErrorHandling
open migrate.Db

type Student = {
    Id: int64
    Name: string
    Email: string option
    EnrollmentDate: DateTime
}

type Student with
  static member Insert (student: Student) (tx: SqliteTransaction) : Result<int64, SqliteException> =
    try
      use cmd = new SqliteCommand("INSERT INTO students (name, email, enrollment_date) VALUES (@name, @email, @enrollment_date)", tx.Connection, tx)
      cmd.Parameters.AddWithValue("@name", student.Name) |> ignore
      cmd.Parameters.AddWithValue("@email", match student.Email with Some v -> box v | None -> box DBNull.Value) |> ignore
      cmd.Parameters.AddWithValue("@enrollment_date", student.EnrollmentDate) |> ignore
      cmd.ExecuteNonQuery() |> ignore
      use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
      let lastId = lastIdCmd.ExecuteScalar() |> unbox<int64>
      Ok lastId
    with
    | :? SqliteException as ex -> Error ex

  static member GetById (id: int64) (tx: SqliteTransaction) : Result<Student option, SqliteException> =
    try
      use cmd = new SqliteCommand("SELECT id, name, email, enrollment_date FROM students WHERE id = @id", tx.Connection, tx)
      cmd.Parameters.AddWithValue("@id", id) |> ignore
      use reader = cmd.ExecuteReader()
      if reader.Read() then
        Ok(Some {
          Id = reader.GetInt64(0)
          Name = reader.GetString(1)
          Email = if reader.IsDBNull(2) then None else Some(reader.GetString(2))
          EnrollmentDate = reader.GetDateTime(3)
        })
      else
        Ok None
    with
    | :? SqliteException as ex -> Error ex

  static member GetAll (tx: SqliteTransaction) : Result<Student list, SqliteException> =
    try
      use cmd = new SqliteCommand("SELECT id, name, email, enrollment_date FROM students", tx.Connection, tx)
      use reader = cmd.ExecuteReader()
      let results = ResizeArray<Student>()
      while reader.Read() do
        results.Add({
          Id = reader.GetInt64(0)
          Name = reader.GetString(1)
          Email = if reader.IsDBNull(2) then None else Some(reader.GetString(2))
          EnrollmentDate = reader.GetDateTime(3)
        })
      Ok(results |> Seq.toList)
    with
    | :? SqliteException as ex -> Error ex

  static member Update (student: Student) (tx: SqliteTransaction) : Result<unit, SqliteException> =
    try
      use cmd = new SqliteCommand("UPDATE students SET name = @name, email = @email, enrollment_date = @enrollment_date WHERE id = @id", tx.Connection, tx)
      cmd.Parameters.AddWithValue("@id", student.Id) |> ignore
      cmd.Parameters.AddWithValue("@name", student.Name) |> ignore
      cmd.Parameters.AddWithValue("@email", match student.Email with Some v -> box v | None -> box DBNull.Value) |> ignore
      cmd.Parameters.AddWithValue("@enrollment_date", student.EnrollmentDate) |> ignore
      cmd.ExecuteNonQuery() |> ignore
      Ok()
    with
    | :? SqliteException as ex -> Error ex

  static member Delete (id: int64) (tx: SqliteTransaction) : Result<unit, SqliteException> =
    try
      use cmd = new SqliteCommand("DELETE FROM students WHERE id = @id", tx.Connection, tx)
      cmd.Parameters.AddWithValue("@id", id) |> ignore
      cmd.ExecuteNonQuery() |> ignore
      Ok()
    with
    | :? SqliteException as ex -> Error ex
```

**Usage Example:**
```fsharp
open migrate.Db

// Option 1: Using Db.txn computation expression (recommended)
Db.txn conn {
  let! id1 = Student.Insert { Id = 0L; Name = "Alice"; Email = Some "alice@example.com"; EnrollmentDate = DateTime.Now }
  let! id2 = Student.Insert { Id = 0L; Name = "Bob"; Email = None; EnrollmentDate = DateTime.Now }
  let! students = Student.GetAll
  return (id1, id2, students)
}

// Option 2: Using Db.WithTransaction explicitly
Db.WithTransaction conn (fun tx ->
  result {
    let! id = Student.Insert { Id = 0L; Name = "Alice"; Email = Some "alice@example.com"; EnrollmentDate = DateTime.Now } tx
    let! student = Student.GetById id tx
    return student
  })

// Option 3: Partial application
let insertAlice = Student.Insert { Id = 0L; Name = "Alice"; Email = Some "alice@example.com"; EnrollmentDate = DateTime.Now }
Db.txn conn {
  let! id = insertAlice  // Transaction automatically applied
  return id
}
```

**CLI Integration:**
- `mig codegen` - Generate F# types from SQL schema files
- `mig commit` - Execute migration and auto-regenerate types (when `-m` message flag is used)

### 4. Custom Query Generation with QueryBy Annotations

In addition to standard CRUD methods, Migrate supports custom query generation through `QueryBy` annotations. These SQL comments allow you to declaratively specify additional query methods based on specific column combinations.

**Syntax:**
```sql
CREATE TABLE table_name (...);
-- QueryBy(column1, column2, ...)
```

**Placement:** QueryBy annotations must appear on the line(s) immediately following a CREATE TABLE or CREATE VIEW statement.

**Features:**
- Multiple QueryBy annotations per table/view supported
- Case-insensitive column name matching
- Generates methods named `GetBy{Column1}{Column2}...`
- Query parameters use **tupled syntax**: `(col1: type1, col2: type2)`
- Transaction parameter remains curried (last parameter)
- Returns `Result<T list, SqliteException>` (list of all matching records)
- Validates column names at code generation time (halts with error if invalid)
- Works with regular tables, normalized tables (discriminated unions), and views

**SQL Example:**
```sql
CREATE TABLE students (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  email TEXT,
  status TEXT NOT NULL,
  enrollment_date TIMESTAMP NOT NULL
);
-- QueryBy(status)
-- QueryBy(name, status)
-- QueryBy(enrollment_date)
```

**Generated F# Code:**
```fsharp
type Student with
  // ... standard CRUD methods (Insert, GetById, GetAll, Update, Delete) ...
  
  static member GetByStatus (status: string) (tx: SqliteTransaction) : Result<Student list, SqliteException> =
    try
      use cmd = new SqliteCommand("SELECT id, name, email, status, enrollment_date FROM students WHERE status = @status", tx.Connection, tx)
      cmd.Parameters.AddWithValue("@status", status) |> ignore
      use reader = cmd.ExecuteReader()
      let results = ResizeArray<Student>()
      while reader.Read() do
        results.Add({
          Id = reader.GetInt64(0)
          Name = reader.GetString(1)
          Email = if reader.IsDBNull(2) then None else Some(reader.GetString(2))
          Status = reader.GetString(3)
          EnrollmentDate = reader.GetDateTime(4)
        })
      Ok(results |> Seq.toList)
    with
    | :? SqliteException as ex -> Error ex
  
  static member GetByNameStatus (name: string, status: string) (tx: SqliteTransaction) : Result<Student list, SqliteException> =
    try
      use cmd = new SqliteCommand("SELECT id, name, email, status, enrollment_date FROM students WHERE name = @name AND status = @status", tx.Connection, tx)
      cmd.Parameters.AddWithValue("@name", name) |> ignore
      cmd.Parameters.AddWithValue("@status", status) |> ignore
      // ... reader logic same as above ...
    with
    | :? SqliteException as ex -> Error ex
  
  static member GetByEnrollmentDate (enrollment_date: DateTime) (tx: SqliteTransaction) : Result<Student list, SqliteException> =
    // ... similar implementation ...
```

**Usage Example:**
```fsharp
open migrate.Db

// Query by single column
Db.txn conn {
  let! activeStudents = Student.GetByStatus "active"
  return activeStudents
}

// Query by multiple columns (note the tupled parameters)
Db.txn conn {
  let! results = Student.GetByNameStatus ("Alice", "active")
  return results
}

// Query by date
Db.txn conn {
  let enrollmentDate = DateTime(2024, 9, 1)
  let! newStudents = Student.GetByEnrollmentDate enrollmentDate
  return newStudents
}

// Partial application works
let getActiveStudents = Student.GetByStatus "active"
Db.txn conn {
  let! students = getActiveStudents
  return students
}
```

**Behavior with Different Table Types:**

1. **Regular Tables (Option-based):**
   - Generates standard SELECT with WHERE clause
   - Returns list of records with option types for nullable columns

2. **Normalized Tables (Discriminated Union-based):**
   - Uses LEFT JOINs to include extension tables
   - Applies NULL checks to determine correct DU case
   - Validates columns across base table AND all extension tables
   - Returns list of discriminated union values

3. **Views:**
   - Generates read-only query methods (no Insert/Update/Delete)
   - Useful for querying across joined data with custom filters

**Validation:**
- Column names are validated at code generation time
- Code generation fails with clear error message if:
  - Column doesn't exist in the table/view
  - Column name is misspelled (case-insensitive matching applied)
- Error message includes list of available columns for easy correction

**Example Validation Error:**
```sql
CREATE TABLE students (id INTEGER, name TEXT);
-- QueryBy(status)  -- ERROR: 'status' column doesn't exist
```

Error output:
```
QueryBy annotation references non-existent column 'status' in table 'students'. 
Available columns: id, name
```

**Implementation Status:**
✅ COMPLETE - Fully implemented and integrated (January 2025)
- SQL comment parsing with FParsec
- QueryBy annotation extraction
- Code generation for all table types (regular, normalized, views)
- Column validation with error reporting
- Tupled parameter syntax for query columns
- Integration with existing CRUD code generation pipeline

### 8. Normalized Schema Representation with Discriminated Unions

For normalized database schemas (2NF) that eliminate NULLs by splitting optional fields into separate tables, Migrate generates F# discriminated unions instead of option types. This approach leverages F#'s type system to represent optional data through table relationships rather than nullable columns.

**Rationale:**

In normalized schemas, the absence of information is represented by the absence of a row in an extension table, not by NULL values. For example:
- A student without a known address: no row in `student_address` table
- A student with a known address: one row in `student_address` table with FK to `student`

This 1:1 optional relationship maps naturally to F# discriminated unions, providing:
- **Type Safety**: Exhaustive pattern matching forces handling of both cases
- **Domain Modeling**: Business semantics are explicit (with/without address)
- **No Option Hell**: Clean pattern matching instead of nested option types
- **Database Design**: Encourages proper normalization and NULL elimination

**Detection Pattern:**

Extension tables are automatically detected by:
1. **Naming Convention**: `{base_table}_{aspect1}_{aspect2}_...`
   - Examples: `student_address`, `student_email_phone`, `user_preferences`
2. **Foreign Key Constraint**: Extension table has FK to base table
3. **1:1 Relationship**: FK column is also the PK of extension table (enforces at most one extension per base record)
4. **No NULLs**: All columns in both base and extension tables must be NOT NULL

**Schema Example:**

```sql
-- Base table
CREATE TABLE student (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL
);

-- Extension table (detected by naming convention)
CREATE TABLE student_address (
  student_id INTEGER PRIMARY KEY REFERENCES student(id),
  address TEXT NOT NULL
);

-- Multiple aspects extension
CREATE TABLE student_email_phone (
  student_id INTEGER PRIMARY KEY REFERENCES student(id),
  email TEXT NOT NULL,
  phone TEXT NOT NULL
);
```

**Generated Code:**

```fsharp
module Students

open System
open Microsoft.Data.Sqlite
open FsToolkit.ErrorHandling
open migrate.Db

// Two discriminated unions: one for inserting (New*), one for querying (*)
[<RequireQualifiedAccess>]
type NewStudent =
  | Base of {| Name: string |}
  | WithAddress of {| Name: string; Address: string |}
  | WithEmailPhone of {| Name: string; Email: string; Phone: string |}

[<RequireQualifiedAccess>]
type Student =
  | Base of {| Id: int64; Name: string |}
  | WithAddress of {| Id: int64; Name: string; Address: string |}
  | WithEmailPhone of {| Id: int64; Name: string; Email: string; Phone: string |}

type Student with
  // Insert with pattern matching on NewStudent
  static member Insert (student: NewStudent) (tx: SqliteTransaction)
    : Result<int64, SqliteException> =
    try
      match student with
      | NewStudent.Base data ->
        // Single INSERT into base table
        use cmd = new SqliteCommand(
          "INSERT INTO student (name) VALUES (@name)",
          tx.Connection, tx)
        cmd.Parameters.AddWithValue("@name", data.Name) |> ignore
        cmd.ExecuteNonQuery() |> ignore
        use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
        let studentId = lastIdCmd.ExecuteScalar() |> unbox<int64>
        Ok studentId

      | NewStudent.WithAddress data ->
        // Two inserts in same transaction (atomic)
        use cmd1 = new SqliteCommand(
          "INSERT INTO student (name) VALUES (@name)",
          tx.Connection, tx)
        cmd1.Parameters.AddWithValue("@name", data.Name) |> ignore
        cmd1.ExecuteNonQuery() |> ignore

        use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
        let studentId = lastIdCmd.ExecuteScalar() |> unbox<int64>

        use cmd2 = new SqliteCommand(
          "INSERT INTO student_address (student_id, address) VALUES (@student_id, @address)",
          tx.Connection, tx)
        cmd2.Parameters.AddWithValue("@student_id", studentId) |> ignore
        cmd2.Parameters.AddWithValue("@address", data.Address) |> ignore
        cmd2.ExecuteNonQuery() |> ignore
        Ok studentId

      | NewStudent.WithEmailPhone data ->
        // Similar pattern for email_phone extension
        // ...
        Ok 0L
    with
    | :? SqliteException as ex -> Error ex

  // GetAll with LEFT JOINs
  static member GetAll (tx: SqliteTransaction)
    : Result<Student list, SqliteException> =
    try
      use cmd = new SqliteCommand(
        "SELECT s.id, s.name, sa.address, sep.email, sep.phone
         FROM student s
         LEFT JOIN student_address sa ON s.id = sa.student_id
         LEFT JOIN student_email_phone sep ON s.id = sep.student_id",
        tx.Connection, tx)
      use reader = cmd.ExecuteReader()
      let results = ResizeArray<Student>()
      while reader.Read() do
        let student =
          let id = reader.GetInt64 0
          let name = reader.GetString 1
          let hasAddress = not (reader.IsDBNull 2)
          let hasEmailPhone = not (reader.IsDBNull 3)

          match hasAddress, hasEmailPhone with
          | true, false ->
            Student.WithAddress {| Id = id; Name = name; Address = reader.GetString 2 |}
          | false, true ->
            Student.WithEmailPhone {| Id = id; Name = name; Email = reader.GetString 3; Phone = reader.GetString 4 |}
          | false, false ->
            Student.Base {| Id = id; Name = name |}
          | true, true ->
            // Cannot have multiple extensions simultaneously in current implementation
            Student.Base {| Id = id; Name = name |}
        results.Add student
      Ok(results |> Seq.toList)
    with
    | :? SqliteException as ex -> Error ex

  // Update with pattern matching
  static member Update (student: Student) (tx: SqliteTransaction)
    : Result<unit, SqliteException> =
    try
      match student with
      | Student.Base data ->
        // Update base table, delete any extensions
        use cmd1 = new SqliteCommand(
          "UPDATE student SET name = @name WHERE id = @id",
          tx.Connection, tx)
        cmd1.Parameters.AddWithValue("@id", data.Id) |> ignore
        cmd1.Parameters.AddWithValue("@name", data.Name) |> ignore
        cmd1.ExecuteNonQuery() |> ignore

        use cmd2 = new SqliteCommand(
          "DELETE FROM student_address WHERE student_id = @id",
          tx.Connection, tx)
        cmd2.Parameters.AddWithValue("@id", data.Id) |> ignore
        cmd2.ExecuteNonQuery() |> ignore

        use cmd3 = new SqliteCommand(
          "DELETE FROM student_email_phone WHERE student_id = @id",
          tx.Connection, tx)
        cmd3.Parameters.AddWithValue("@id", data.Id) |> ignore
        cmd3.ExecuteNonQuery() |> ignore
        Ok()

      | Student.WithAddress data ->
        // Update base, INSERT OR REPLACE extension
        use cmd1 = new SqliteCommand(
          "UPDATE student SET name = @name WHERE id = @id",
          tx.Connection, tx)
        cmd1.Parameters.AddWithValue("@id", data.Id) |> ignore
        cmd1.Parameters.AddWithValue("@name", data.Name) |> ignore
        cmd1.ExecuteNonQuery() |> ignore

        use cmd2 = new SqliteCommand(
          "INSERT OR REPLACE INTO student_address (student_id, address) VALUES (@student_id, @address)",
          tx.Connection, tx)
        cmd2.Parameters.AddWithValue("@student_id", data.Id) |> ignore
        cmd2.Parameters.AddWithValue("@address", data.Address) |> ignore
        cmd2.ExecuteNonQuery() |> ignore
        Ok()

      | Student.WithEmailPhone data ->
        // Similar pattern
        // ...
        Ok()
    with
    | :? SqliteException as ex -> Error ex

  // Delete (cascades to extensions via FK)
  static member Delete (id: int64) (tx: SqliteTransaction)
    : Result<unit, SqliteException> =
    try
      use cmd = new SqliteCommand(
        "DELETE FROM student WHERE id = @id",
        tx.Connection, tx)
      cmd.Parameters.AddWithValue("@id", id) |> ignore
      cmd.ExecuteNonQuery() |> ignore
      Ok()
    with
    | :? SqliteException as ex -> Error ex
```

**Usage Example:**

```fsharp
open Students
open migrate.Db

// Insert students with different variants
Db.txn conn {
  // Student without additional info
  let! id1 = Student.Insert (NewStudent.Base {| Name = "Alice" |})

  // Student with address
  let! id2 = Student.Insert (
    NewStudent.WithAddress {| Name = "Bob"; Address = "123 Main St" |})

  // Student with contact info
  let! id3 = Student.Insert (
    NewStudent.WithEmailPhone {| Name = "Carol"; Email = "carol@example.com"; Phone = "555-1234" |})

  // Query all students
  let! students = Student.GetAll

  // Pattern match on results
  for student in students do
    match student with
    | Student.Base data ->
      printfn "Student %d: %s (no additional info)" data.Id data.Name
    | Student.WithAddress data ->
      printfn "Student %d: %s at %s" data.Id data.Name data.Address
    | Student.WithEmailPhone data ->
      printfn "Student %d: %s - %s, %s" data.Id data.Name data.Email data.Phone

  return students
}
```

**Key Design Decisions:**

1. **Two Discriminated Unions**:
   - `NewStudent` for insertion (no ID)
   - `Student` for queries (with ID)
   - This reflects auto-increment PK semantics

2. **Anonymous Records**:
   - Uses F# anonymous records for DU cases
   - Avoids polluting namespace with extra type definitions
   - Clean syntax with structural typing

3. **RequireQualifiedAccess**:
   - Forces qualified access: `Student.Base`, `NewStudent.WithAddress`
   - Prevents name collision between union cases
   - Makes code more explicit

4. **Union Case Naming**:
   - Base case: `Base`
   - Extension case: `With{Aspect}` where aspect is PascalCase from table suffix
   - Example: `student_email_phone` → `WithEmailPhone`

5. **Transaction Atomicity**:
   - Multi-table inserts happen in single transaction
   - FK constraints enforce referential integrity
   - Rollback on any failure

6. **Multiple Extensions**:
   - Each extension table generates one union case
   - Multiple extensions create multiple cases (not combinatorial)
   - Example: `Base | WithAddress | WithEmailPhone` (3 cases, not 4)

7. **Error on NULLs**:
   - If ANY column in base or extension tables is nullable, code generation fails with error
   - Enforces normalization discipline
   - Clear error message guides user to fix schema

**Detection Algorithm:**

```
For each table T in schema:
  1. Check if T has any nullable columns
     - If yes: Skip DU generation for this table (use option types instead)

  2. Find potential extension tables E:
     - E.name matches pattern "{T.name}_{aspect1}_{aspect2}_..."
     - E has single-column PK that is also FK to T.pk
     - E has no nullable columns

  3. If extension tables found:
     - Generate NewT discriminated union (for insert)
     - Generate T discriminated union (for query)
     - Generate case for base table: Base
     - Generate case for each extension: With{AspectName}

  4. Generate CRUD methods with pattern matching
```

**Limitations:**

- Only supports one active extension per student at a time (no combinatorial cases)
- Extension tables must follow exact naming convention
- All columns must be NOT NULL (nullable columns cause generation error)
- Manual schema migration if changing from option-based to DU-based representation

**Convenience Properties:**

Generated discriminated unions include convenience properties that expose all fields:

```fsharp
type Student with
  // Common fields (in all cases) - non-optional
  member this.Id : int64 =
    match this with
    | Student.Base data -> data.Id
    | Student.WithAddress data -> data.Id

  member this.Name : string =
    match this with
    | Student.Base data -> data.Name
    | Student.WithAddress data -> data.Name

  // Partial fields (only in some cases) - optional
  member this.Address : string option =
    match this with
    | Student.Base _ -> None
    | Student.WithAddress data -> Some data.Address
```

This allows accessing fields without manual pattern matching: `student.Address` returns `string option`.

**Implementation Status:**

✅ **COMPLETE** - Fully implemented and integrated (January 2025)

The normalized schema feature has been fully implemented with:
- Automatic detection of extension tables with comprehensive validation
- Discriminated union type generation (New* and query types)
- Convenience properties for all fields (common and partial)
- Complete CRUD operations with pattern matching (Insert, GetAll, GetById, GetOne, Update, Delete)
- Comprehensive error handling with actionable suggestions
- Integration with `mig codegen` CLI command
- 65 tests covering all functionality

Statistics display in CLI:
```
$ mig codegen
Code generation complete!

Statistics:
  Normalized tables (DU): 1
  Regular tables (records): 2
  Views: 0

Generated files:
  ...
```

### 9. Seed Data Management with Idempotent Upserts

**Purpose:** Execute SQL INSERT statements as idempotent upserts using primary keys, enabling safe and repeatable database seeding.

**Command:** `mig seed`

#### Overview

The seed feature allows you to define initial data for your database using INSERT statements placed directly in your SQL schema files. These seeds are executed as `INSERT OR REPLACE` operations, making them idempotent—they can be run multiple times without errors.

#### How It Works

1. **INSERT Statement Parsing**: The SQL parser extracts INSERT statements from your `.sql` files alongside CREATE TABLE and other schema definitions.

2. **Primary Key Validation**: Only tables with primary keys are seeded. Tables without PKs are skipped with warning messages. This requirement ensures `INSERT OR REPLACE` can properly detect and update existing rows.

3. **Dependency Ordering**: INSERT statements are automatically ordered based on foreign key relationships. Child tables (those with foreign key constraints) are seeded after their parent tables, preventing constraint violations.

4. **Atomic Transaction**: All seed operations execute within a single database transaction. If any INSERT fails, the entire seed operation is rolled back, maintaining database consistency.

5. **Upsert Semantics**: `INSERT OR REPLACE` allows seed operations to be idempotent. Running `mig seed` multiple times produces the same result without errors—new rows are inserted, and existing rows are updated.

#### Schema File Example

```sql
CREATE TABLE users (
  id INTEGER PRIMARY KEY,
  email TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  created_at TEXT DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE posts (
  id INTEGER PRIMARY KEY,
  user_id INTEGER NOT NULL,
  title TEXT NOT NULL,
  body TEXT,
  FOREIGN KEY (user_id) REFERENCES users(id)
);

-- Seed data (executed with mig seed, ignored during migrations)
INSERT INTO users(id, email, name) VALUES (1, 'alice@example.com', 'Alice');
INSERT INTO users(id, email, name) VALUES (2, 'bob@example.com', 'Bob');

INSERT INTO posts(id, user_id, title, body)
  VALUES (1, 1, 'Hello World', 'My first post');
INSERT INTO posts(id, user_id, title, body)
  VALUES (2, 1, 'Second Post', 'Another post from Alice');
INSERT INTO posts(id, user_id, title, body)
  VALUES (3, 2, 'Bob''s Post', 'A post from Bob');
```

#### Workflow

**Step 1: Define schema and seed data**
```bash
# schema.sql contains both CREATE TABLE and INSERT statements
mig status  # Preview the migration
```

**Step 2: Apply migrations to create tables**
```bash
mig commit  # Executes CREATE TABLE statements
```

**Step 3: Seed the database**
```bash
mig seed    # Executes INSERT OR REPLACE statements
```

**Step 4: Re-seed if needed (idempotent)**
```bash
mig seed    # Can be run multiple times safely
```

#### Execution Output

```
✅ 0
INSERT OR REPLACE INTO users(id, email, name) VALUES (1, 'alice@example.com', 'Alice')

✅ 1
INSERT OR REPLACE INTO users(id, email, name) VALUES (2, 'bob@example.com', 'Bob')

✅ 2
INSERT OR REPLACE INTO posts(id, user_id, title, body) VALUES (1, 1, 'Hello World', 'My first post')

✅ 3
INSERT OR REPLACE INTO posts(id, user_id, title, body) VALUES (2, 1, 'Second Post', 'Another post from Alice')

✅ 4
INSERT OR REPLACE INTO posts(id, user_id, title, body) VALUES (3, 2, 'Bob''s Post', 'A post from Bob')

✅ Successfully seeded 5 table(s)
```

#### Key Features

- **Automatic Dependency Resolution**: Foreign key relationships are analyzed to determine correct execution order
- **Primary Key Enforcement**: Upsert semantics require primary keys; tables without them are skipped
- **Atomic Operations**: All seeds execute in a single transaction; any failure rolls back all changes
- **Idempotent Execution**: Run seeds multiple times without errors or data corruption
- **Multiple Values Support**: Single INSERT statement with multiple value tuples: `VALUES (1), (2), (3)`
- **Error Reporting**: Clear error messages for missing tables, constraint violations, and FK failures

#### Limitations

- **Single-Column PKs**: Works best with single-column primary keys (composite PKs supported but less common)
- **No Multi-Table Transactions**: Each INSERT is a separate operation; all-or-nothing semantics apply to all seeds together
- **FK-Only Ordering**: Only foreign key constraints are used for ordering; other dependencies (views, triggers) don't affect seed order

#### Implementation Status

✅ COMPLETE - Fully implemented and integrated (January 2025)

- ✅ INSERT statement parsing with FParsec
- ✅ Primary key validation and filtering
- ✅ Foreign key-based dependency ordering with topological sort
- ✅ `INSERT OR REPLACE` SQL generation
- ✅ Atomic transaction-based execution with rollback
- ✅ `mig seed` CLI command
- ✅ Idempotent upsert behavior
- ✅ Multi-row INSERT support
- ✅ Warning messages for skipped tables

## Architecture

### High-Level Components

```
┌─────────────────────────────────────────┐
│         CLI Interface (mig)             │
├─────────────────────────────────────────┤
│                                         │
│  ┌──────────────────────────────────┐   │
│  │    MigLib (Core Library)         │   │
│  │                                  │   │
│  │  ┌────────────────────────────┐  │   │
│  │  │ DeclarativeMigrations      │  │   │
│  │  │ - SqlParser                │  │   │
│  │  │ - Solve (dependency logic) │  │   │
│  │  │ - GenerateSql              │  │   │
│  │  │ - Migration orchestration  │  │   │
│  │  └────────────────────────────┘  │   │
│  │                                  │   │
│  │  ┌────────────────────────────┐  │   │
│  │  │ Execution                  │  │   │
│  │  │ - Database connections     │  │   │
│  │  │ - SQL execution            │  │   │
│  │  │ - Schema reading           │  │   │
│  │  └────────────────────────────┘  │   │
│  │                                  │   │
│  │  ┌────────────────────────────┐  │   │
│  │  │ MigrationLog               │  │   │
│  │  │ - History tracking         │  │   │
│  │  │ - Execution logging        │  │   │
│  │  └────────────────────────────┘  │   │
│  │                                  │   │
│  │  ┌────────────────────────────┐  │   │
│  │  │ CodeGen                    │  │   │
│  │  │ - TypeGenerator            │  │   │
│  │  │ - QueryGenerator           │  │   │
│  │  │ - ProjectGenerator         │  │   │
│  │  │ - FileMapper               │  │   │
│  │  │ - FabulousAstHelpers       │  │   │
│  │  └────────────────────────────┘  │   │
│  │                                  │   │
│  └──────────────────────────────────┘  │
│                                         │
└─────────────────────────────────────────┘
         │
         ▼
    SQLite Database
         │
         ▼
   Generated F# Code
```

### Key Modules

#### SqlParser.fs
**Purpose:** Parse SQL schema definitions into typed structures

**Key Functions:**
- `parse(file, sql)` - Main entry point, returns `Result<SqlFile, string>`
- `parseCreateTable(text)` - Extract table definition
- `parseCreateView(text)` - Extract view definition
- `parseCreateIndex(text)` - Extract index definition
- `parseCreateTrigger(text)` - Extract trigger definition
- `extractViewDependencies(sqlTokens)` - Identify view dependencies
- `splitStatements(sql)` - Split SQL text into individual statements

**Input:** Raw SQL text
**Output:** Typed AST with tables, views, indexes, triggers, and their constraints

#### Types.fs
**Purpose:** Define domain types for schema representation

**Key Types:**
- `SqlType` - Enumeration of SQL data types
- `ColumnDef` - Column definition with constraints
- `ColumnConstraint` - Individual constraint types
- `CreateTable`, `CreateView`, `CreateIndex`, `CreateTrigger` - Schema elements
- `SqlFile` - Container for all parsed schema elements
- `MigrationError` - Error handling types

#### Solve.fs
**Purpose:** Compare schemas and determine required migrations

**Key Functions:**
- `sortFile(sqlFile)` - Topological sort with dependency resolution
- `tableDifferences(actual, expected)` - Find table changes
- `columnMigrations(actual, expected)` - Determine column operations
- `tableMigrationsSql(actual, expected)` - Generate table migration SQL
- `viewMigrationsSql(views, actual, expected)` - Generate view migration SQL
- `indexMigrationsSql(actual, expected)` - Generate index migration SQL
- `triggerMigrationSql(actual, expected)` - Generate trigger migration SQL

**Algorithm:**
1. Parse both schemas into typed structures
2. Compare elements by name and structure
3. Classify changes (add, drop, rename, modify)
4. Resolve dependencies via topological sort
5. Generate SQL for each change in dependency order

#### GenerateSql.fs
**Purpose:** Generate executable SQL from migration operations

**Functions:**
- `Table.createSql(table)` - Generate CREATE TABLE statement
- `Table.dropSql(name)` - Generate DROP TABLE statement
- `Table.columnDefSql(column)` - Generate column definition with constraints
- `Table.constraintSql(constraint)` - Generate constraint SQL

**Output:** Formatted, executable SQL statements

#### Exec.fs
**Purpose:** Connect to database and execute migrations

**Key Functions:**
- `executeMigration(db, statements)` - Execute SQL statements with error handling
- `readSchema(db)` - Read current schema from sqlite_master
- `migrationStatements(sqlFiles)` - Parse schema files and generate migrations

**Features:**
- Transaction management
- Schema caching
- Error recovery

#### ExecAndLog.fs
**Purpose:** Execute migrations and record history

**Key Functions:**
- `generateMigrationScript(db, expectedSchema)` - Generate and log migrations
- `initializeMigrationLog(db)` - Create migration history table

**Schema Tracking:** Stores executed migrations in `_schema_migration` table

#### TypeGenerator.fs
**Purpose:** Generate F# record types from SQL table definitions

**Key Functions:**
- `generateType(table)` - Create F# record type from table schema
- `mapSqlType(sqlType, isNullable)` - Map SQL types to F# types
- `generateFieldDefinitions(columns)` - Create record fields with proper types

**Type Mapping:**
- INTEGER → int64 (option if nullable)
- TEXT → string (option if nullable)
- REAL → float (option if nullable)
- TIMESTAMP → DateTime (option if nullable)
- STRING → string (option if nullable)

**Output:** Fabulous.AST record type definitions

#### QueryGenerator.fs
**Purpose:** Generate transaction-based CRUD methods and JOIN queries

**Key Functions:**
- `getPrimaryKey(table)` - Extract primary key columns (supports both column-level and table-level PKs)
- `generateWithTransaction(table)` - Create generic WithTransaction helper (entry point)
- `generateInsert(table)` - Create INSERT method taking `SqliteTransaction`
- `generateUpdate(table)` - Create UPDATE method taking `SqliteTransaction` (supports composite PKs)
- `generateDelete(table)` - Create DELETE method taking `SqliteTransaction` (supports composite PKs)
- `generateGet(table)` - Create SELECT by primary key method taking `SqliteTransaction` (supports composite PKs)
- `generateGetAll(table)` - Create SELECT all method taking `SqliteTransaction`
- `generateJoinQueries(table, foreignKeys)` - Create JOIN methods for related entities (planned)

**Design:** All CRUD methods require `SqliteTransaction` parameter, using `tx.Connection` internally. This enforces transactional operations and provides a consistent API.

**Features:**
- Transaction-only API (all methods require `SqliteTransaction`)
- Parameterized queries (SQL injection protection)
- Result type error handling
- Proper resource disposal (use statements)
- Option type handling for nullable values
- Composite primary key support (e.g., `PRIMARY KEY(col1, col2)`)

**Output:** F# method definitions as strings

#### ProjectGenerator.fs
**Purpose:** Generate .fsproj file for generated code

**Key Functions:**
- `generateProjectFile(projectName)` - Create .fsproj with proper dependencies
- `addSourceFiles(files)` - Include generated F# files in compilation order

**Generated Project:**
- References Microsoft.Data.Sqlite
- References FsToolkit.ErrorHandling
- Targets .NET 10.0

#### FileMapper.fs
**Purpose:** Map SQL files to F# module files

**Key Functions:**
- `mapSqlToModule(sqlFile)` - Convert students.sql → Students.fs
- `determineModuleName(sqlFileName)` - Capitalize first letter for module name
- `ensureColocatedOutput(sqlFile)` - Write F# file in same directory as SQL

**Naming Convention:**
- SQL file: `students.sql` → F# module: `Students.fs`
- SQL file: `course_enrollments.sql` → F# module: `CourseEnrollments.fs`

#### FabulousAstHelpers.fs
**Purpose:** Helper functions for working with Fabulous.AST

**Key Functions:**
- `createModule(name, declarations)` - Create F# module structure
- `createRecordType(name, fields)` - Create record type definition
- `createStaticMethod(name, parameters, returnType, body)` - Create static method
- `createOpenDirective(namespace)` - Add open statements
- `generateSourceFile(module)` - Convert AST to F# source code string

**Fabulous.AST Integration:**
- Type-safe F# code generation
- No string concatenation for code generation
- Proper indentation and formatting
- Syntax correctness guaranteed by AST

### Data Flow

#### Migration Flow
```
SQL Schema Files
       │
       ▼
   SqlParser
       │
       ▼
   Typed AST (SqlFile)
       │
    ┌──┴──┐
    │     │
    ▼     ▼
 Actual Expected
 Schema  Schema
    │     │
    └──┬──┘
       ▼
   Solve Module
       │
       ▼
  Migration Operations
  (Create/Drop/Rename)
       │
       ▼
  GenerateSql Module
       │
       ▼
   SQL Statements
       │
       ▼
   Exec Module
       │
       ▼
   Database
```

#### Code Generation Flow
```
SQL Schema Files
       │
       ▼
   SqlParser
       │
       ▼
   Typed AST (SqlFile)
       │
       ▼
   FileMapper
       │
       ▼
SQL File → Module Name Mapping
       │
       ▼
   TypeGenerator
       │
       ▼
F# Record Types (Fabulous.AST)
       │
       ▼
   QueryGenerator
       │
       ▼
CRUD Methods + JOINs (Fabulous.AST)
       │
       ▼
   FabulousAstHelpers
       │
       ▼
Complete F# Source Files
       │
       ▼
   ProjectGenerator
       │
       ▼
.fsproj File + Generated F# Files
```

## Design Decisions

### 1. FParsec Parser for SQL Parsing
**Decision:** Implement an FParsec-based parser in F# instead of using ANTLR4 with generated C# code

**Rationale:**
- Reduces dependency chain (no ANTLR code generation required)
- Keeps entire codebase in F# for consistency
- Parser combinators provide robust error recovery and proper backtracking
- FParsec is idiomatic F# with excellent composability
- Easier to maintain and extend with typed AST builders
- No build-time code generation complexity

**Trade-off:** Additional dependency (FParsec) adds ~200KB to binary, but provides superior error recovery and maintainability compared to regex-based approach

**Status:** ✅ Fully implemented. Handles CREATE TABLE, CREATE VIEW, CREATE INDEX, CREATE TRIGGER with comprehensive constraint support.

### 2. Declarative vs. Imperative Migrations
**Decision:** Use declarative approach where users define target schema, not individual migration steps

**Rationale:**
- Reduces manual migration writing effort
- Fewer migration bugs from human error
- Easier to understand desired state vs. transformation steps
- Automatic SQL generation is more reliable for schema-to-schema transformations

**Trade-off:** Less control over specific migration details; manual scripts required for data transformation

### 3. Dependency Resolution via Topological Sort
**Decision:** Automatically resolve dependencies and topologically sort operations

**Rationale:**
- Eliminates manual ordering of DDL statements
- Handles complex dependency graphs automatically
- Prevents foreign key constraint violations
- Ensures views created after their source tables

**Implementation:** Graph-based topological sort with cycle detection

### 4. Table Recreation for Complex Changes
**Decision:** Use CREATE TABLE with RENAME strategy for column changes with constraints

**Rationale:**
- SQLite's limited ALTER TABLE support
- Preserves data integrity
- Handles constraint changes safely
- More reliable than trying to modify constraints in-place

**Trade-off:** More expensive for large tables, requires data copy

### 5. Central Package Management
**Decision:** Use .NET Central Package Management for dependency versions

**Rationale:**
- Consistent versions across all projects
- Easier to upgrade dependencies
- Prevents version conflicts
- Better for monorepo management

### 6. Library and CLI Distribution
**Decision:** Distribute as both NuGet library (MigLib) and CLI tool (migtool)

**Rationale:**
- Supports standalone CLI usage for teams without .NET expertise
- Provides library for programmatic integration
- Single codebase serves multiple use cases

### 7. Fabulous.AST for Code Generation
**Decision:** Use Fabulous.AST library for type-safe F# code generation instead of string templates

**Rationale:**
- **Type Safety:** AST-based generation guarantees syntactically correct F# code
- **No String Concatenation:** Eliminates entire class of code generation bugs
- **Maintainability:** Changes to generated code structure are easier to implement
- **Formatting:** Automatic proper indentation and F# idioms
- **Refactoring:** AST transformations are safer than text transformations

**Trade-off:** Additional dependency and learning curve, but benefits far outweigh costs for code generation quality

### 8. Static Methods for CRUD Operations
**Decision:** Generate static methods on types rather than separate repository classes

**Rationale:**
- **Simplicity:** `Student.Insert(conn, student)` is more direct than `StudentRepository.Insert(student)`
- **Discoverability:** Methods appear with type definition, easier for IDE autocomplete
- **No State:** Static methods make it clear there's no hidden state or configuration
- **F# Idiomatic:** Aligns with F# preference for functions over objects

**Trade-off:** Less flexibility for dependency injection patterns, but appropriate for lightweight data access

### 9. Result Types for Error Handling
**Decision:** All database operations return `Result<T, SqliteException>` instead of throwing exceptions

**Rationale:**
- **Explicit Error Handling:** Compiler enforces error handling at call sites
- **F# Idiomatic:** Result types are standard F# error handling approach
- **Composability:** Works naturally with FsToolkit.ErrorHandling computation expressions
- **No Silent Failures:** Impossible to ignore errors accidentally

**Implementation:** Use `result { }` computation expressions for clean error propagation

### 10. Colocation of SQL and F# Files
**Decision:** Generate F# files in same directory as SQL schema files

**Rationale:**
- **File System Proximity:** Related files sort alphabetically near each other (students.sql, Students.fs)
- **Discoverability:** Easy to find generated code for a given SQL schema
- **Single Source of Truth:** SQL schema and generated types live together
- **Simple Mental Model:** One SQL file → one F# module, same location

**Alternative Considered:** Separate src/generated directory was rejected for being less discoverable

### 11. Raw ADO.NET Over Micro-ORMs
**Decision:** Generate code using Microsoft.Data.Sqlite directly, no Dapper/FsSql/etc.

**Rationale:**
- **Minimal Dependencies:** Only SQLite driver required, no additional abstractions
- **Full Control:** Complete visibility into SQL execution and parameter handling
- **Performance:** No overhead from mapping layers
- **Simplicity:** Generated code is straightforward ADO.NET, easy to understand and debug
- **Learning Curve:** Developers familiar with any database library will understand the code

**Trade-off:** More verbose generated code, but generated code doesn't need to be manually written anyway

## Dependencies

**NuGet Packages:**
- `FSharp.Core` 10.0.100 - F# runtime
- `FsToolkit.ErrorHandling` 4.18.0 - Monadic error handling
- `Microsoft.Data.Sqlite` 9.0.0 - SQLite database driver
- `SqlPrettify` 1.0.3 - SQL formatting and syntax highlighting
- `FSharpPlus` 1.6.1 - Functional programming utilities
- `dotenv.net` 3.2.1 - Environment variable loading
- `Argu` 6.2.4 - Command-line argument parsing
- `Fabulous.AST` (latest) - Type-safe F# code generation
- `FParsec` 1.1.1 - Parser combinators (available for future enhancements)

**Build Tools:**
- .NET SDK 10.0.0
- F# compiler (included with .NET SDK)

## Limitations and Known Issues

### Current Limitations
1. **Views with UNION clauses** - Known issue with dependency detection (see code comments)
2. **WITH statements** - Partial support for CTEs
3. **Complex expressions** - Stored as token sequences rather than fully analyzed
4. **Single RDBMS** - Only SQLite supported (though architecture allows extension)
5. **Manual data migrations** - Cannot automatically transform data during schema changes
6. **Many-to-Many Relationships** - JOIN queries only for direct foreign keys currently

### Not Supported (SQLite Limitations)
- Stored procedures (SQLite doesn't support)
- Sequences (SQLite doesn't support)
- Partial indexes
- Generated columns (SQLite 3.31+)
- JSON functions

## Future Enhancements

### Planned Features (Code Generation)
1. **Many-to-Many Relationships** - Generate bridge table query helpers
2. **Async Database Operations** - Generate async/Task-based methods alongside synchronous ones
3. **Connection String Management** - Helper functions for connection lifecycle
4. **Advanced QueryBy Features** - Support for additional SQL clauses (ORDER BY, LIMIT, OFFSET)

### Planned Features (Migration)
1. Support for PostgreSQL, MySQL/MariaDB
2. Better error messages and diagnostics
3. Dry-run with preview of generated SQL
4. Rollback capability with migration history
5. Automatic data transformation suggestions

### Potential Improvements
1. Migration templates for common patterns
2. Integration with version control systems
3. Web UI for migration management
4. Performance optimizations for large schemas
5. Generated code optimization (cached SqliteCommand instances)
6. Query result streaming for large datasets
7. Bulk insert operations
8. Combinatorial cases for normalized schemas (multiple active extensions)
9. Flexible naming patterns for extension tables (via configuration)
10. Automated migration from option-based to DU-based representation

## Testing Strategy

**Test Coverage:** 86 tests (all passing)

**Migration Tests:**
- Table migration tests - verifies DDL generation for various schema changes
- View migration tests - ensures dependency ordering for views
- Library usage tests - confirms programmatic API functionality
- Composite primary key tests - validates multi-column PK support

**Code Generation Tests:**
- Type generation tests - verifies correct F# record type generation from SQL
- CRUD method tests - ensures generated Insert/Update/Delete/Get methods compile and work
- Transaction helper tests - confirms transaction methods work correctly
- Type mapping tests - verifies SQL type → F# type conversions
- Null handling tests - ensures option types generated for nullable columns
- View code generation tests - validates read-only GetAll and GetOne for views
- Project file generation tests - validates .fsproj structure and dependencies

**Normalized Schema Tests (65 tests):**
- Detection and validation tests (11) - extension table detection, FK validation
- Type generation tests (8) - DU generation, case naming
- Property generation tests (7) - convenience properties for common/partial fields
- Insert query tests (8) - pattern matching, multi-table inserts
- Read query tests (8) - LEFT JOINs, case selection, GetAll/GetById/GetOne
- Update/Delete query tests (9) - case transitions, FK cascades
- Validation error tests (10) - error messages with actionable suggestions
- Integration tests (5) - end-to-end code generation with statistics

**Test Framework:** xUnit
**Run Tests:** `cd src && dotnet test`

**Code Generation Test Strategy:**
1. Generate code from test schemas
2. Compile generated code to ensure syntactic correctness
3. Execute generated code against test database
4. Verify query results match expected data
5. Test error handling paths (constraint violations, etc.)

## Performance Considerations

**Migration Performance:**
- Schema parsing: < 100ms for most schemas
- Dependency resolution: O(n log n) with topological sort
- SQL generation: < 50ms for moderate schemas
- Database execution: Depends on data volume and constraint checking

**Code Generation Performance:**
- Schema parsing: < 100ms (reuses migration parser)
- Type generation: < 50ms per table (using Fabulous.AST)
- Method generation: < 100ms per table (CRUD + JOINs)
- File writing: < 10ms per module
- Project file generation: < 50ms
- **Total for medium schema (20 tables):** < 5 seconds

**Generated Code Performance:**
- CRUD operations: Direct ADO.NET, minimal overhead
- Parameterized queries: Prevents SQL injection, slight prep overhead
- Result type wrapping: Negligible performance impact
- No reflection or dynamic code: Fully AOT-compatible

**Optimization Opportunities:**
- Cache parsed schemas between migration and codegen
- Parallel code generation for multiple tables
- Incremental regeneration (only changed schemas)
- Connection pooling in generated code (future enhancement)
