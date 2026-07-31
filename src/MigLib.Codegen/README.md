# MigLib.Codegen

Dev-time / CLI codegen for migrate:

- `generate migrationsDir outputDir namespace` — apply SQL migrations to a temp DB, introspect, emit one `.fs` module per annotated relation **and** `Migrations.fs` with those scripts as F# string constants for runtime `migrateScripts`

Depends on **MigLib** for migrate helpers and SQLite.

Used by `mig codegen` and app `build.fsx` scripts. Production AOT apps should reference **MigLib** only; migrations ship as compiled constants in the app, not via resources or reflection.
