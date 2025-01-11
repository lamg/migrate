# Execution

Implements getting the database schema, executing a migration step by step and generating a migration script

## Get database schema

In a directory `test_db` with the following structure

```
test_db
.
└── test_db.sqlite
```

The command

```sh
mig schema
```

outputs the schema of the database in SQL and with syntax highlighting. In order to deactivate the syntax highlighting pass the `-n` flag before any subcommand. This is useful when creating a SQL script because it removes the ANSI color sequences from the output.

## Execute migration

In a directory with the following structure

```
test_db
.
└── schema.sql
```

the command

```sh
mig exec
```

leaves the following structure:

```
test_db
.
├── schema.sql
└── test_db.sqlite
```

Now `test_db.sqlite` has the desired schema. In case `test_db.sqlite` already exists it will generate only the necessary steps in order to migrate the database.

## Generate script

In a directory with the following structure

```
test_db
.
├── schema.sql
└── test_db.sqlite
```

the command

```sh
mig gen
```

will output a script that allows to review and migrate the `test_db.sqlite` database, which will be created in case it doesn't exist.

## Migration execution using MigLib

MigLib is a library designed for other projects to run migrations relying on internal source code and not in the command line interface `mig`. The usage is as follows:

```fsharp
open migrate.Execution.Exec
open FsToolkit.ErrorHandling

let sources = [
  { name = "schema0.sql"; content="CREATE TABLE table0(id INTEGER NOT NULL)" }
] 

result {
  let! statements = migrationStatementsForDb ("/path/to/db.sqlite", sources)
  let! results = executeMigration statements
  return results
}
```