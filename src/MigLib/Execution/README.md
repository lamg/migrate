# Migration Execution

This module handles:
- Reading and analyzing database schemas
- Generating migration scripts by comparing expected vs actual schemas
- Executing migrations step-by-step
- Displaying database schemas

## Overview

Migrate performs **declarative migrations** by comparing your expected schema (defined in SQL files) with your actual database, then generating and executing the necessary migration steps.

## Database File Location

By default, the database file is `<directory-name>.sqlite` in the current directory.

You can override this by setting the `migrate_db` environment variable:

```sh
# Using .env file
echo "migrate_db=/path/to/my-database.sqlite" > .env
mig commit

# Or inline
migrate_db=/path/to/custom.db mig commit
```

## Commands

### Show Database Schema

Display the current database schema with SQL syntax highlighting:

```sh
mig schema
```

**Disable syntax highlighting** (useful for piping to files):

```sh
mig -nc schema > current_schema.sql
```

**Example output:**

```sql
CREATE TABLE students (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    email TEXT
);

CREATE INDEX idx_students_email ON students(email);
```

### Preview Migration Changes

Generate a migration script without executing it:

```sh
mig status
```

This shows what SQL statements would be executed to migrate your database.

**Example:**

```
Initial directory:
test_db/
├── schema.sql          -- Expected schema
└── test_db.sqlite      -- Current database

Running: mig status

Output:
ALTER TABLE students ADD COLUMN phone TEXT;
CREATE INDEX idx_students_phone ON students(phone);
```

### Execute Migration

Generate and execute migration steps automatically:

```sh
mig commit
```

**With a commit message** (logged for audit trail):

```sh
mig commit -m "Add phone column to students table"
```

**Without logging** (not recommended for production):

```sh
mig -nl commit
```

**What happens:**
1. Reads all `.sql` files in current directory
2. Compares expected schema with actual database
3. Generates migration steps
4. Executes each step
5. Logs execution (unless `-nl` flag is used)

**Example:**

```
Initial directory:
test_db/
└── schema.sql

After: mig commit

Result:
test_db/
├── schema.sql
└── test_db.sqlite      -- Created with schema from schema.sql
```

### View Migration History

Show all executed migrations:

```sh
mig log
```

**Show specific migration steps** by ID (date shown in log):

```sh
mig log -s 2024-01-15T10:30:00Z
```

## Multiple SQL Files

All `.sql` files in the directory are processed and merged:

```
project/
├── tables.sql          -- Table definitions
├── views.sql           -- View definitions
└── indexes.sql         -- Index definitions
```

The order of processing is:
1. Tables
2. Views (depend on tables)
3. Indexes
4. Inserts
5. Triggers

## Using MigLib as a Library

Integrate migration execution into your F# applications:

### Basic Usage

```fsharp
open migrate.Execution.Exec
open FsToolkit.ErrorHandling

// Define SQL sources (can be from files, embedded resources, etc.)
let sources = [
  { name = "schema.sql"
    content = "CREATE TABLE students(id INTEGER PRIMARY KEY, name TEXT NOT NULL)" }
  { name = "views.sql"
    content = "CREATE VIEW active_students AS SELECT * FROM students" }
]

// Execute migration
result {
  let! statements = migrationStatementsForDb ("/path/to/db.sqlite", sources)
  let! results = executeMigration statements
  return results
}
|> function
  | Ok messages ->
    messages |> List.iter (printfn "✓ %s")
  | Error err ->
    eprintfn "Migration failed: %A" err
```

### Advanced Usage with Error Handling

```fsharp
open migrate.Execution.Exec
open migrate.Execution.Types
open FsToolkit.ErrorHandling

let runMigration dbPath sqlFiles =
  result {
    // Read SQL files
    let! sources =
      sqlFiles
      |> List.traverseResultM (fun path ->
        result {
          let! content = readFile path
          return { name = path; content = content }
        })

    // Generate migration statements
    let! statements = migrationStatementsForDb (dbPath, sources)

    // Check if migration is needed
    if List.isEmpty statements then
      printfn "No migration needed - database is up to date"
      return []
    else
      printfn "Executing %d migration statements..." statements.Length

      // Execute migration
      let! results = executeMigration statements
      return results
  }
  |> function
    | Ok results ->
      printfn "Migration completed successfully"
      results |> List.iter (printfn "  %s")
      0
    | Error (FailedSteps failures) ->
      eprintfn "Migration failed with errors:"
      failures |> List.iter (eprintfn "  %s")
      1
    | Error err ->
      eprintfn "Migration error: %A" err
      1

// Usage
runMigration "/data/app.db" ["schema.sql"; "views.sql"]
```

### Integration with Logging

```fsharp
open migrate.Execution.ExecAndLog
open FsToolkit.ErrorHandling

let runMigrationWithLog dbPath message =
  result {
    let! statements = migrationStatements ()
    let! results = executeMigrations (Some message, statements)
    return results
  }
```

## Error Handling

MigLib returns `Result<'T, MigrationError>` where errors can be:

- `OpenDbFailed` - Database connection failed
- `ReadSchemaFailed` - Failed to read current schema
- `ReadFileFailed` - Failed to read SQL file
- `ParsingFailed` - SQL parsing error
- `FailedSteps` - Migration execution failed (contains list of failures)
- `Composed` - Multiple errors occurred

## Environment Variables

- `migrate_db` - Override default database file path

## Files and Conventions

- **SQL files**: All `.sql` files in the current directory are processed
- **Database**: `<directory-name>.sqlite` by default
- **Migration log**: Stored in the database (see [MigrationLog](../MigrationLog/README.md))

## See Also

- [Declarative Migrations](../DeclarativeMigrations/README.md) - SQL schema definition format
- [Migration Log](../MigrationLog/README.md) - Viewing and managing migration history
- [Code Generation](../CodeGen/README.md) - Generate F# types from schemas
