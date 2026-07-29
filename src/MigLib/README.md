# MigLib

SQL-first SQLite helpers for F#:

- `dbTxn` / `readOnlyDbTxn` / `txn` computation expressions (`TxnStep` on `SqliteTransaction`)
- `Query` helpers shared with generated code
- `generate` — emit one `.fs` module file per annotated relation into an output directory
- `migrateScripts` / `migrateEmbedded` — run DbUp upgrades

See the repository root README and `specs/sql_first_rewrite.md`.
