# Db Layers

This directory contains the database-facing modules used by generated code, transaction helpers, and migration workflows.

There is no single `Db.fs` facade file in the current layout. Public consumers work directly with the relevant modules, primarily:

- `MigLib.Db.Attributes`
- `MigLib.Db.Transactions`
- `MigLib.Db.Recording`

## Layers

### Attributes

- schema and query-generation attributes consumed by compiled schema reflection

### Core

- low-level SQLite helpers
- path normalization
- shared marker-table helpers
- transaction context storage used by higher layers

### Recording

- transaction readiness checks for generated CRUD usage
- migration marker detection
- write recording for workflows that still use marker tables internally

### Transactions

- `TxnStep`
- `DbTxnBuilder`
- `DbRuntime`
- `txn`
- `dbTxn`

## Dependency Direction

`Attributes -> Core -> Recording -> Transactions`

Keep new code in the lowest layer that can own the responsibility cleanly.
