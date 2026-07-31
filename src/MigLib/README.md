# MigLib

SQL-first SQLite **runtime** helpers for F#:

- `dbTxn` / `readOnlyDbTxn` / `txn` computation expressions (`TxnStep` on `SqliteTransaction`)
- `Query` helpers shared with generated code
- `migrateScripts` — apply ordered named scripts `(scriptName, sql) list` (`SchemaVersions` journal); pass codegen’s `Migrations.scripts` (compile-time F# strings in the app binary; AOT-friendly, no reflection)

For Result / TaskResult CEs, use **`Lamg.Env.Result`** (`open Lamg.Env.Result`).

Codegen (`generate`) lives in the separate **MigLib.Codegen** package.

See the repository root README and `specs/sql_first_rewrite.md`.
