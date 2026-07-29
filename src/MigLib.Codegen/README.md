# MigLib.Codegen

Dev-time / CLI codegen for migrate:

- `generate migrationsDir outputDir namespace` — apply SQL migrations to a temp DB, introspect, emit one `.fs` module per annotated relation

Depends on **MigLib** for `migrateScripts` and SQLite.

Used by `mig codegen` and app `build.fsx` scripts. Production AOT apps should reference **MigLib** only.
