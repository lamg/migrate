# Operator Runbook

This runbook describes the current blocking migration workflow.

## Scope

- source: an existing schema-bound SQLite file for the same app/instance prefix
- target: the generated current-schema SQLite file
- migration mode: blocking command-driven copy and archive

## Preflight

1. Confirm backups exist for the current SQLite file.
2. Build the domain modeling project.
3. Run `mig codegen`.
4. Build the runtime project so the compiled runtime assembly includes the generated module.
5. Run `mig plan` and resolve any unsupported differences before migrating.

Example:

```sh
dotnet build ./my-app/DomainModeling/DomainModeling.fsproj
mig codegen -d ./my-app
dotnet build ./my-app/my-app.fsproj
mig plan -d ./my-app
```

## Initialize a Fresh Database

Use this when no previous schema-bound database exists yet:

```sh
mig init -d ./my-app
```

Expected result:

- the target database exists
- schema seed rows have been applied

## Migrate an Existing Database

Use this when exactly one older schema-bound database exists for the same app/instance prefix:

```sh
mig migrate -d ./my-app
```

Expected result:

- the new target database exists
- data has been copied into the target
- the previous database has been marked readonly and moved into `archive/`

## Validate Current State

```sh
mig status -d ./my-app
```

Check:

- `Current database` points at the current schema-bound file
- `Archived databases` includes the previous file after migration
- `Needs migration: no` once only the current target remains in the main database directory

## Recovery

If you need to discard the current target and restore the latest archived database:

```sh
mig reset -d ./my-app
```

Expected result:

- the current target database is removed
- the most recent archived database is moved back into the main database directory
- the `_mig_readonly` marker is removed from the restored database

## Operational Notes

- `mig` resolves database files by app prefix, instance, and schema hash.
- The generated runtime module exposes `GeneratedSchema`; application code should resolve schema-bound database paths through MigLib project resolution instead of hardcoded filenames.
- `mig migrate` is the workflow that creates the new target and archives the previous source. There are no separate `drain`, `cutover`, or `archive-old` commands in the current CLI.
