# Migrate - Database Migration Tool & F# Type Generator Specification

## Overview

Migrate is a declarative database migration tool and F# type generator for SQLite. It automatically generates and executes SQL statements to transform an actual database schema into an expected schema, and generates type-safe F# code for database access. Rather than requiring developers to write migration scripts or data access code manually, Migrate compares schemas, generates DDL statements, and produces F# types with CRUD operations.

**Version:** 2.0.0 (planned - major feature: F# code generation)
**Target Framework:** .NET 10.0
**Language:** F#
**Database:** SQLite
**License:** Apache 2.0

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
**Implementation:** Pure F# regex-based parser (no external parser generators)

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
- `mig gen` - Generate migration SQL (dry-run)
- `mig status` - Show migration status
- `mig exec` - Execute generated migration and auto-regenerate F# types
- `mig commit` - Commit migration to database
- `mig log` - Display migration history and metadata
- `mig schema` - Show current database schema
- `mig codegen` - Generate F# types from SQL schema files

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
- **CRUD Operations:**
  - `Insert` - Returns `Result<int64, SqliteException>` with last_insert_rowid
  - `Update` - Returns `Result<unit, SqliteException>`
  - `Delete` - Returns `Result<unit, SqliteException>`
  - `Get` - Returns `Result<T option, SqliteException>`
  - `GetAll` - Returns `Result<T list, SqliteException>`
- **Foreign Key Queries:** Automatic generation of methods to query related entities via JOINs
- **Transaction Support:** `WithTransaction` helper methods for atomic operations
- **Error Handling:** All operations return `Result<T, SqliteException>`

**Implementation:**
- Uses [Fabulous.AST](https://github.com/edgarfgp/Fabulous.AST) for type-safe F# code generation
- Uses raw ADO.NET (Microsoft.Data.Sqlite) for database operations
- Generated code colocated with SQL files in same directory

**Example Generated Code:**
```fsharp
module Students

open Microsoft.Data.Sqlite
open FsToolkit.ErrorHandling

type Student = {
    Id: int64
    Name: string
    Email: string option
    EnrollmentDate: DateTime
}

type Student with
    static member Insert(conn: SqliteConnection, student: Student) : Result<int64, SqliteException> =
        result {
            use cmd = new SqliteCommand("INSERT INTO students (name, email, enrollment_date) VALUES (@name, @email, @enrollment_date)", conn)
            cmd.Parameters.AddWithValue("@name", student.Name) |> ignore
            cmd.Parameters.AddWithValue("@email", Option.toObj student.Email) |> ignore
            cmd.Parameters.AddWithValue("@enrollment_date", student.EnrollmentDate) |> ignore
            do! cmd.ExecuteNonQuery() |> ignore
            let lastId = conn.LastInsertRowId
            return lastId
        }

    static member GetById(conn: SqliteConnection, id: int64) : Result<Student option, SqliteException> =
        result {
            use cmd = new SqliteCommand("SELECT id, name, email, enrollment_date FROM students WHERE id = @id", conn)
            cmd.Parameters.AddWithValue("@id", id) |> ignore
            use reader = cmd.ExecuteReader()
            if reader.Read() then
                return Some {
                    Id = reader.GetInt64(0)
                    Name = reader.GetString(1)
                    Email = if reader.IsDBNull(2) then None else Some (reader.GetString(2))
                    EnrollmentDate = reader.GetDateTime(3)
                }
            else
                return None
        }

    static member WithTransaction(conn: SqliteConnection, action: SqliteTransaction -> Result<'T, SqliteException>) : Result<'T, SqliteException> =
        result {
            use transaction = conn.BeginTransaction()
            try
                let! result = action transaction
                transaction.Commit()
                return result
            with
            | :? SqliteException as ex ->
                transaction.Rollback()
                return! Error ex
        }
```

**CLI Integration:**
- `mig codegen` - Generate F# types from SQL schema files
- `mig exec` - Execute migration and auto-regenerate types

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
**Purpose:** Generate CRUD methods and JOIN queries

**Key Functions:**
- `generateInsert(table)` - Create INSERT method returning Result<int64, SqliteException>
- `generateUpdate(table)` - Create UPDATE method
- `generateDelete(table)` - Create DELETE method
- `generateGet(table)` - Create SELECT by primary key method
- `generateGetAll(table)` - Create SELECT all method
- `generateJoinQueries(table, foreignKeys)` - Create JOIN methods for related entities
- `generateTransactionHelper()` - Create WithTransaction method

**Features:**
- Parameterized queries (SQL injection protection)
- Result type error handling
- Proper resource disposal (use statements)
- Option type handling for nullable values

**Output:** Fabulous.AST method definitions

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

### 1. Pure F# Parser Instead of ANTLR4
**Decision:** Implement a regex-based parser in F# instead of using ANTLR4 with generated C# code

**Rationale:**
- Reduces dependency chain (no ANTLR code generation required)
- Keeps entire codebase in F# for consistency
- Regex patterns are sufficient for SQLite dialect
- Easier to maintain and extend
- No build-time code generation complexity

**Trade-off:** Less robust error recovery compared to full parser generator, but adequate for controlled SQL input from schema files

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
6. **Composite Primary Keys** - Code generation deferred for future implementation
7. **Many-to-Many Relationships** - JOIN queries only for direct foreign keys currently

### Not Supported (SQLite Limitations)
- Stored procedures (SQLite doesn't support)
- Sequences (SQLite doesn't support)
- Partial indexes
- Generated columns (SQLite 3.31+)
- JSON functions

## Future Enhancements

### Planned Features (Code Generation)
1. **Composite Primary Keys** - Support tables with multi-column primary keys
2. **Many-to-Many Relationships** - Generate bridge table query helpers
3. **Custom Query Generation** - Allow users to define additional queries in annotations
4. **Async Database Operations** - Generate async/Task-based methods alongside synchronous ones
5. **Connection String Management** - Helper functions for connection lifecycle

### Planned Features (Migration)
1. Support for PostgreSQL, MySQL/MariaDB
2. Better error messages and diagnostics
3. Dry-run with preview of generated SQL
4. Rollback capability with migration history
5. Automatic data transformation suggestions

### Potential Improvements
1. Full SQL parser using parser combinators (FParsec)
2. Migration templates for common patterns
3. Integration with version control systems
4. Web UI for migration management
5. Performance optimizations for large schemas
6. Generated code optimization (cached SqliteCommand instances)
7. Query result streaming for large datasets
8. Bulk insert operations

## Testing Strategy

**Test Coverage:**

**Migration Tests:**
- Table migration tests - verifies DDL generation for various schema changes
- View migration tests - ensures dependency ordering for views
- Library usage tests - confirms programmatic API functionality
- Parser tests - validates SQL parsing accuracy

**Code Generation Tests:**
- Type generation tests - verifies correct F# record type generation from SQL
- CRUD method tests - ensures generated Insert/Update/Delete/Get methods compile and work
- JOIN query tests - validates foreign key relationship query generation
- Transaction helper tests - confirms transaction methods work correctly
- Type mapping tests - verifies SQL type → F# type conversions
- Null handling tests - ensures option types generated for nullable columns
- Project file generation tests - validates .fsproj structure and dependencies
- Integration tests - end-to-end tests from SQL schema to working F# code

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
