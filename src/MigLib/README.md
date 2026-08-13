# MigLib

SQL-first SQLite **runtime** helpers for F#:

- `dbTxn` / `readOnlyDbTxn` / `txn` computation expressions (`TxnStep` on `SqliteTransaction`)
- `Query` helpers shared with generated code
- `migrate` — apply snapshot SQL plus an optional hop, verify the live catalog, return a `DbTxnBuilder`

Apps should call generated `{namespace}.Migration.migrate dbPath`.

For Result / TaskResult CEs, use **`Lamg.Env.Result`** (`open Lamg.Env.Result`).

Codegen (`generate`) lives in the separate **MigLib.Codegen** package.

See the repository root README and `specs/schema_dir_and_migration.md`.
