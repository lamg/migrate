# Runtime Layers

This directory contains runtime support modules used by generated code, transaction helpers, and migration workflows.

Most public consumers should use `MigLib.MigProject` instead of opening these modules directly. Generated code opens `MigLib.Runtime` and uses the runtime modules primarily through:

- `MigLib.Runtime.TxnStep`

## Layers

### Core

- low-level SQLite helpers
- path normalization

### TxnStep

- `TxnStep`
- transaction execution internals used by `MigLib.MigProject` public builders

## Dependency Direction

`Core -> TxnStep`

Keep new code in the lowest layer that can own the responsibility cleanly.
