# Migrate - Database Migration Tool Specification

## Overview

Migrate is a declarative database migration tool for SQLite that automatically generates and executes SQL statements to transform an actual database schema into an expected schema. Rather than requiring developers to write migration scripts manually, Migrate compares the two schemas and generates the necessary DDL statements.

**Version:** 1.0.5
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
- `mig exec` - Execute generated migration
- `mig commit` - Commit migration to database
- `mig log` - Display migration history and metadata
- `mig schema` - Show current database schema
- `mig import` - Import Goose migrations

**Library Interface:**
- MigLib NuGet package for programmatic access
- Supports integration into C# or F# applications

### 6. Migration Logging
Tracks all executed migrations with:
- Migration timestamp
- SQL statements executed
- Success/failure status
- Metadata storage in `_schema_migration` table

### 7. Goose Migration Import
Supports importing existing Goose-format migrations:
- Parses Goose migration file format
- Converts to Migrate format
- Maintains migration history consistency

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
│  └──────────────────────────────────┘  │
│                                         │
└─────────────────────────────────────────┘
         │
         ▼
    SQLite Database
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

### Data Flow

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

## Dependencies

**NuGet Packages:**
- `FSharp.Core` 9.0.100 - F# runtime
- `FsToolkit.ErrorHandling` 4.18.0 - Monadic error handling
- `Microsoft.Data.Sqlite` 9.0.0 - SQLite database driver
- `SqlPrettify` 1.0.3 - SQL formatting and syntax highlighting
- `FSharpPlus` 1.6.1 - Functional programming utilities
- `dotenv.net` 3.2.1 - Environment variable loading
- `Argu` 6.2.4 - Command-line argument parsing

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

### Not Supported
- Stored procedures (SQLite doesn't support)
- Sequences (SQLite doesn't support)
- Partial indexes
- Generated columns (SQLite 3.31+)
- JSON functions

## Future Enhancements

### Planned Features
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

## Testing Strategy

**Test Coverage:**
- Table migration tests - verifies DDL generation for various schema changes
- View migration tests - ensures dependency ordering for views
- Library usage tests - confirms programmatic API functionality
- Parser tests - validates SQL parsing accuracy

**Test Framework:** xUnit
**Run Tests:** `cd src && dotnet test`

## Performance Considerations

**Typical Performance:**
- Schema parsing: < 100ms for most schemas
- Dependency resolution: O(n log n) with topological sort
- SQL generation: < 50ms for moderate schemas
- Database execution: Depends on data volume and constraint checking

**Optimization Opportunities:**
- Cache parsed schemas
- Parallel migration testing for large schemas
- Incremental schema comparison
