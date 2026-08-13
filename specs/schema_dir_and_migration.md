# Schema directory and `_migration.sql`

`schema/` is the desired SQLite catalog. Codegen compiles it into `Migration.migrate dbPath`. At runtime that function brings the live database to the catalog or fails.

## Layout

```
schema/
  _migration.sql           # hop: previous catalog → this catalog (root only)
  users.sql
  items.sql
  views/active_user.sql
```

Every `*.sql` file except root `_migration.sql` is the **snapshot**. Nested directories are allowed. Files join in ordinal relative-path order. A nested `_migration.sql` is snapshot SQL, not a hop.

`-- mig:` annotations go on snapshot `CREATE` statements.

## Runtime

`Migration.migrate dbPath` compares the live catalog to the snapshot (objects plus `PRAGMA table_info`: name, type, notnull, pk — not `CREATE` text, not data).

| Live catalog    | Action                              |
| --------------- | ----------------------------------- |
| Already matches | No-op                               |
| Empty           | Apply snapshot, then verify         |
| Anything else   | Apply `_migration.sql`, then verify |

A missing or empty hop on a non-empty database is an error. A hop that does not produce the snapshot catalog is rolled back. Older databases are not upgraded automatically; recover by hand or rebuild.

Git is the authoring history. MigLib only reads the current schema directory.

## API

Apps call the generated function. SQL stays private.

```fsharp
let! db = Migration.migrate dbPath
```

The library function used by that wrapper and by tests:

```fsharp
val migrate:
  dbPath: string ->
  expectedSchema: string ->
  migrationSql: string ->
  Task<Result<DbTxnBuilder, MigError>>
```
