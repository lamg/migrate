# Runtime Layers

This directory contains runtime support modules used by generated code, transaction helpers, and migration workflows.

Most public consumers should use `MigLib.DbProject` instead of opening these modules directly. Generated code opens `MigLib.Runtime` and uses the runtime modules primarily through:

- `MigLib.Runtime.TxnStep`
- `MigLib.Runtime.Recording`

## Layers

### Core

- low-level SQLite helpers
- path normalization
- shared marker-table helpers
- transaction context storage used by higher layers

### Recording

- transaction readiness checks for generated CRUD usage
- migration marker detection
- write recording for workflows that still use marker tables internally

### TxnStep

- `TxnStep`
- transaction execution internals used by `MigLib.DbProject` public builders

## Dependency Direction

`Core -> Recording -> TxnStep`

Keep new code in the lowest layer that can own the responsibility cleanly.
