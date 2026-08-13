# MigLib.Codegen

Dev-time / CLI codegen for migrate:

- `generate schemaDir outputDir namespace` — apply the schema snapshot to a temp DB, introspect, emit one `.fs` module per annotated relation **and** `Migration.fs` with `let migrate dbPath`

Depends on **MigLib** for migrate helpers and SQLite.

Used by `mig codegen` and app `build.fsx` scripts. Production AOT apps should reference **MigLib** only; schema SQL ships as compiled constants inside `Migration.migrate`.
