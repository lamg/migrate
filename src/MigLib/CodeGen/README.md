# F# Code Generation

Migrate's code generation feature transforms your SQL schema files into type-safe F# code with CRUD operations, providing a lightweight ORM experience with full compile-time safety.

## Overview

The code generator:
- Reads your SQL schema files (CREATE TABLE and CREATE VIEW statements)
- Generates F# record types with proper nullable handling
- Generates CRUD methods with curried signatures for clean functional composition
- Creates a ready-to-use F# project with all necessary dependencies
- Supports transactions through computation expressions

## Quick Start

```sh
# Generate F# code from schema files in current directory
mig codegen

# Generate from a specific directory
mig codegen -d ./schema
```

This creates:
- One `.fs` file for each `.sql` file (e.g., `students.sql` → `Students.fs`)
- A `.fsproj` project file ready to build
- All files use the shared `migrate.Db` module for transaction management

## Generated Code Structure

### Input: SQL Schema

**students.sql:**
```sql
CREATE TABLE students (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    email TEXT,
    enrollment_date TIMESTAMP NOT NULL
);

CREATE VIEW adult_students AS
    SELECT id, name
    FROM students
    WHERE age >= 18;
```

### Output: F# Code

**Students.fs:**
```fsharp
module Students

open System
open Microsoft.Data.Sqlite
open FsToolkit.ErrorHandling
open migrate.Db

// Record type with proper nullable handling
type Students = {
    Id: int64 option
    Name: string
    Email: string option
    EnrollmentDate: DateTime
}

// CRUD methods as type extensions
type Students with
  static member Insert (item: Students) (tx: SqliteTransaction)
    : Result<int64, SqliteException> =
    try
      use cmd = new SqliteCommand(
        "INSERT INTO students (name, email, enrollment_date) VALUES (@name, @email, @enrollment_date)",
        tx.Connection, tx)
      cmd.Parameters.AddWithValue("@name", item.Name) |> ignore
      cmd.Parameters.AddWithValue("@email",
        match item.Email with Some v -> box v | None -> box DBNull.Value) |> ignore
      cmd.Parameters.AddWithValue("@enrollment_date", item.EnrollmentDate) |> ignore
      cmd.ExecuteNonQuery() |> ignore
      use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
      let lastId = lastIdCmd.ExecuteScalar() |> unbox<int64>
      Ok lastId
    with :? SqliteException as ex -> Error ex

  static member GetById (id: int64) (tx: SqliteTransaction)
    : Result<Students option, SqliteException> =
    try
      use cmd = new SqliteCommand(
        "SELECT id, name, email, enrollment_date FROM students WHERE id = @id",
        tx.Connection, tx)
      cmd.Parameters.AddWithValue("@id", id) |> ignore
      use reader = cmd.ExecuteReader()
      if reader.Read() then
        Ok(Some {
          Id = if reader.IsDBNull 0 then None else Some(reader.GetInt64 0)
          Name = reader.GetString 1
          Email = if reader.IsDBNull 2 then None else Some(reader.GetString 2)
          EnrollmentDate = reader.GetDateTime 3
        })
      else Ok None
    with :? SqliteException as ex -> Error ex

  static member GetAll (tx: SqliteTransaction)
    : Result<Students list, SqliteException> =
    try
      use cmd = new SqliteCommand(
        "SELECT id, name, email, enrollment_date FROM students",
        tx.Connection, tx)
      use reader = cmd.ExecuteReader()
      let results = ResizeArray<Students>()
      while reader.Read() do
        results.Add({
          Id = if reader.IsDBNull 0 then None else Some(reader.GetInt64 0)
          Name = reader.GetString 1
          Email = if reader.IsDBNull 2 then None else Some(reader.GetString 2)
          EnrollmentDate = reader.GetDateTime 3
        })
      Ok(results |> Seq.toList)
    with :? SqliteException as ex -> Error ex

  static member GetOne (tx: SqliteTransaction)
    : Result<Students option, SqliteException> =
    try
      use cmd = new SqliteCommand(
        "SELECT id, name, email, enrollment_date FROM students LIMIT 1",
        tx.Connection, tx)
      use reader = cmd.ExecuteReader()
      if reader.Read() then
        Ok(Some {
          Id = if reader.IsDBNull 0 then None else Some(reader.GetInt64 0)
          Name = reader.GetString 1
          Email = if reader.IsDBNull 2 then None else Some(reader.GetString 2)
          EnrollmentDate = reader.GetDateTime 3
        })
      else Ok None
    with :? SqliteException as ex -> Error ex

  static member Update (item: Students) (tx: SqliteTransaction)
    : Result<unit, SqliteException> =
    try
      use cmd = new SqliteCommand(
        "UPDATE students SET name = @name, email = @email, enrollment_date = @enrollment_date WHERE id = @id",
        tx.Connection, tx)
      cmd.Parameters.AddWithValue("@id",
        match item.Id with Some v -> box v | None -> box DBNull.Value) |> ignore
      cmd.Parameters.AddWithValue("@name", item.Name) |> ignore
      cmd.Parameters.AddWithValue("@email",
        match item.Email with Some v -> box v | None -> box DBNull.Value) |> ignore
      cmd.Parameters.AddWithValue("@enrollment_date", item.EnrollmentDate) |> ignore
      cmd.ExecuteNonQuery() |> ignore
      Ok()
    with :? SqliteException as ex -> Error ex

  static member Delete (id: int64) (tx: SqliteTransaction)
    : Result<unit, SqliteException> =
    try
      use cmd = new SqliteCommand(
        "DELETE FROM students WHERE id = @id",
        tx.Connection, tx)
      cmd.Parameters.AddWithValue("@id", id) |> ignore
      cmd.ExecuteNonQuery() |> ignore
      Ok()
    with :? SqliteException as ex -> Error ex

// View record type
type AdultStudents = {
  Id: int64
  Name: string
}

// Read-only methods for views
type AdultStudents with
  static member GetAll (tx: SqliteTransaction)
    : Result<AdultStudents list, SqliteException> =
    try
      use cmd = new SqliteCommand(
        "SELECT id, name FROM adult_students",
        tx.Connection, tx)
      use reader = cmd.ExecuteReader()
      let results = ResizeArray<AdultStudents>()
      while reader.Read() do
        results.Add({
          Id = reader.GetInt64 0
          Name = reader.GetString 1
        })
      Ok(results |> Seq.toList)
    with :? SqliteException as ex -> Error ex

  static member GetOne (tx: SqliteTransaction)
    : Result<AdultStudents option, SqliteException> =
    try
      use cmd = new SqliteCommand(
        "SELECT id, name FROM adult_students LIMIT 1",
        tx.Connection, tx)
      use reader = cmd.ExecuteReader()
      if reader.Read() then
        Ok(Some {
          Id = reader.GetInt64 0
          Name = reader.GetString 1
        })
      else Ok None
    with :? SqliteException as ex -> Error ex
```

## Using Generated Code

### 1. Transaction Computation Expression (Recommended)

The `txn` computation expression from `migrate.Db` provides clean syntax for database operations:

```fsharp
open System
open Microsoft.Data.Sqlite
open migrate.Db
open Students

let insertAndQuery (connString: string) =
  use conn = new SqliteConnection(connString)
  conn.Open()

  // Use txn computation expression
  Db.txn conn {
    // Insert a new student
    let newStudent = {
      Id = None
      Name = "Alice Johnson"
      Email = Some "alice@example.com"
      EnrollmentDate = DateTime.Now
    }
    let! studentId = Students.Insert newStudent

    // Query the inserted student
    let! maybeStudent = Students.GetById studentId

    // Get all students
    let! allStudents = Students.GetAll

    // Update the student
    let updatedStudent = { newStudent with Id = Some studentId; Email = Some "alice.j@example.com" }
    do! Students.Update updatedStudent

    // Query views
    let! adultStudents = AdultStudents.GetAll

    return (studentId, allStudents, adultStudents)
  }
  |> function
    | Ok (id, students, adults) ->
      printfn "Created student ID: %d" id
      printfn "Total students: %d" students.Length
      printfn "Adult students: %d" adults.Length
    | Error ex ->
      printfn "Error: %s" ex.Message
```

### 2. Explicit WithTransaction

For more control, use `Db.WithTransaction` explicitly:

```fsharp
open migrate.Db
open FsToolkit.ErrorHandling

let queryExample (connString: string) =
  use conn = new SqliteConnection(connString)
  conn.Open()

  Db.WithTransaction conn (fun tx ->
    result {
      let! students = Students.GetAll tx
      let! firstStudent = Students.GetOne tx
      return (students, firstStudent)
    })
```

### 3. Partial Application

Curried signatures enable partial application:

```fsharp
let insertStudent name email =
  let student = {
    Id = None
    Name = name
    Email = email
    EnrollmentDate = DateTime.Now
  }
  Students.Insert student  // Returns: SqliteTransaction -> Result<int64, SqliteException>

// Use in transaction
Db.txn conn {
  let! id1 = insertStudent "Bob" (Some "bob@example.com")
  let! id2 = insertStudent "Carol" None
  return (id1, id2)
}
```

### 4. Composing Operations

```fsharp
let createAndEnrollStudent name email courseId =
  Db.txn conn {
    // Insert student
    let newStudent = {
      Id = None
      Name = name
      Email = email
      EnrollmentDate = DateTime.Now
    }
    let! studentId = Students.Insert newStudent

    // Create enrollment
    let enrollment = {
      StudentId = studentId
      CourseId = courseId
      EnrollmentDate = DateTime.Now
    }
    let! enrollmentId = Enrollments.Insert enrollment

    return (studentId, enrollmentId)
  }
```

### 5. Error Handling

All methods return `Result<'T, SqliteException>` for explicit error handling:

```fsharp
let safeQuery studentId =
  Db.txn conn {
    let! maybeStudent = Students.GetById studentId
    match maybeStudent with
    | Some student ->
      printfn "Found: %s" student.Name
      return student
    | None ->
      return! Error (SqliteException("Student not found", 404))
  }
  |> function
    | Ok student -> printfn "Success: %A" student
    | Error ex -> printfn "Failed: %s - Code: %d" ex.Message ex.ErrorCode
```

## Type Mappings

SQL types are mapped to F# types as follows:

| SQL Type | F# Type (NOT NULL) | F# Type (nullable) |
|----------|-------------------|-------------------|
| INTEGER  | `int64`           | `int64 option`    |
| TEXT     | `string`          | `string option`   |
| REAL     | `float`           | `float option`    |
| TIMESTAMP| `DateTime`        | `DateTime option` |

## Composite Primary Keys

Tables with composite primary keys generate methods with multiple parameters:

**SQL:**
```sql
CREATE TABLE enrollments (
    student_id INTEGER NOT NULL,
    course_id INTEGER NOT NULL,
    grade TEXT,
    PRIMARY KEY(student_id, course_id)
);
```

**Generated Methods:**
```fsharp
// Curried parameters for composite PK
static member GetById (studentId: int64) (courseId: int64) (tx: SqliteTransaction)
  : Result<Enrollments option, SqliteException>

static member Delete (studentId: int64) (courseId: int64) (tx: SqliteTransaction)
  : Result<unit, SqliteException>
```

**Usage:**
```fsharp
Db.txn conn {
  let! enrollment = Enrollments.GetById 1L 42L
  do! Enrollments.Delete 1L 42L
  return enrollment
}
```

## Views (Read-Only)

Views generate only `GetAll` and `GetOne` methods (no Insert/Update/Delete):

```fsharp
Db.txn conn {
  let! adults = AdultStudents.GetAll
  let! oneAdult = AdultStudents.GetOne
  return adults
}
```

Views support complex SQL including JOINs, CTEs, and aggregations. Column types and nullability are determined by SQLite introspection.

## Generated Methods Reference

### For Tables

| Method | Signature | Description |
|--------|-----------|-------------|
| `Insert` | `(item: T) -> (tx: SqliteTransaction) -> Result<int64, SqliteException>` | Insert record, returns last inserted ID |
| `GetById` | `(pk1: T1) -> ... -> (tx: SqliteTransaction) -> Result<T option, SqliteException>` | Get by primary key (supports composite PKs) |
| `GetAll` | `(tx: SqliteTransaction) -> Result<T list, SqliteException>` | Get all records |
| `GetOne` | `(tx: SqliteTransaction) -> Result<T option, SqliteException>` | Get first record (LIMIT 1) |
| `Update` | `(item: T) -> (tx: SqliteTransaction) -> Result<unit, SqliteException>` | Update record by primary key |
| `Delete` | `(pk1: T1) -> ... -> (tx: SqliteTransaction) -> Result<unit, SqliteException>` | Delete by primary key (supports composite PKs) |

### For Views

| Method | Signature | Description |
|--------|-----------|-------------|
| `GetAll` | `(tx: SqliteTransaction) -> Result<T list, SqliteException>` | Get all records |
| `GetOne` | `(tx: SqliteTransaction) -> Result<T option, SqliteException>` | Get first record (LIMIT 1) |

## Building Generated Code

The generated project includes all necessary dependencies:

```sh
cd generated_project_directory
dotnet build
```

Dependencies included in generated `.fsproj`:
- `FSharp.Core` 10.0.100
- `FsToolkit.ErrorHandling` 4.18.0
- `Microsoft.Data.Sqlite` 9.0.0
- `migtool` 2.0.0 (includes `migrate.Db` module)

## Design Decisions

### Curried Signatures with Transaction Last

All methods use curried signatures with `SqliteTransaction` as the last parameter. This enables:
- Clean computation expression syntax (transaction auto-supplied)
- Partial application for reusable functions
- Functional composition patterns

### Result Types

All methods return `Result<'T, SqliteException>` for:
- Explicit error handling
- Railway-oriented programming patterns
- Composition with FsToolkit.ErrorHandling

### Shared Transaction Management

The `Db` module is shared across all generated code (part of MigLib 2.0):
- No code duplication
- Consistent transaction handling
- Both `WithTransaction` and `txn` computation expression available

### Static Methods on Types

Methods are generated as static members on record types for:
- Clean namespacing
- IntelliSense discoverability
- F# idiomatic patterns

## Advanced Examples

### Batch Operations

```fsharp
let insertMultipleStudents students =
  Db.txn conn {
    let! ids =
      students
      |> List.traverseResultM (fun s -> Students.Insert s)
    return ids
  }
```

### Conditional Updates

```fsharp
let updateIfExists studentId newEmail =
  Db.txn conn {
    let! maybeStudent = Students.GetById studentId
    match maybeStudent with
    | Some student ->
      let updated = { student with Email = newEmail }
      do! Students.Update updated
      return Some updated
    | None ->
      return None
  }
```

### Complex Queries

```fsharp
let getStudentsWithEnrollments () =
  Db.txn conn {
    let! students = Students.GetAll
    let! enrollments = Enrollments.GetAll

    let studentsWithEnrollments =
      students
      |> List.map (fun s ->
        let studentEnrollments =
          enrollments
          |> List.filter (fun e -> e.StudentId = s.Id)
        (s, studentEnrollments))

    return studentsWithEnrollments
  }
```

## Limitations

- No JOIN query generation (planned for future)
- SQLite only (PostgreSQL support planned)
- Simple CRUD operations (complex queries require manual SQL)

## See Also

- [Declarative Migrations](../DeclarativeMigrations/README.md)
- [PROGRESS.md](../../../PROGRESS.md) for implementation details
- [spec.md](../../../spec.md) for full specification
