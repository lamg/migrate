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

outputs the schema of the database in SQL and with syntax highlighting. In order to deactivate the syntax highlighting pass the
`-n` flag before any subcommand. This is useful when creating a SQL script because it removes the ANSI color sequences from the output.

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

Now `test_db.sqlite` has the desired schema. In case
`test_db.sqlite` already exists it will generate only the necessary steps in order to migrate the database.

## Generate script

In a directory with the following structure

```
test_db
.
├── schema.sql
└── test_db.sqlite
```

It will output a script that allows to review and migrate the
`test_db.sqlite` database, which will be created in case it doesn't exist.