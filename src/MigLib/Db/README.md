# Db Layers

This directory contains the internal layering behind the public `MigLib.Db` module.

`../Db.fs` remains the module entry file so the public API stays under the same module name. The implementation is split here by responsibility.

## Layers

### Attributes

Responsibilities:

- define the public schema and query attributes consumed by schema reflection and code generation
- expose shared constants such as `Rfc3339UtcNow`

This is the public metadata surface of the database module.

### Core

Responsibilities:

- manage low-level SQLite initialization and connection helpers
- normalize database paths
- provide shared internal helpers for status-table access and recorded row serialization
- define transaction context types used by higher layers

This is the lowest operational layer.

### Startup

Responsibilities:

- inspect target database state during startup
- decide whether to use, wait for, or migrate a target database
- wait for another process to complete startup migration work

This layer builds on `Core` and exposes startup-facing decisions publicly.

### Recording

Responsibilities:

- validate whether transactions may write while migration states are active
- detect recording or draining modes from migration marker tables
- flush recorded writes into `_migration_log`
- expose the `MigrationLog` helpers used by generated code

This layer depends on `Core`.

### Transactions

Responsibilities:

- define `TxnStep` and the transaction computation expressions
- run transactions with readiness checks and migration recording behavior
- expose `DbRuntime`, `DbTxnBuilder`, `TxnBuilder`, and helper constructors

This is the top operational layer for `MigLib.Db` and depends on all lower layers it needs.

## Dependency Direction

The intended direction is:

`Attributes -> Core -> Startup/Recording -> Transactions -> ../Db.fs`

Lower layers should not depend on higher layers. Keep new code in the lowest layer that can own the responsibility cleanly.
