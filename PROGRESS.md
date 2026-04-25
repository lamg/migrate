# Progress

## Direction

Replace the current hot-migration model with a simpler blocking startup migration model.

The new behavior is:

1. `startService` checks whether the schema-bound target database already exists and is usable.
2. If the target database exists, it is used immediately.
3. If no target database exists and no previous database exists, a fresh database is initialized.
4. If a previous database exists, `startService`:
   - marks the old database readonly
   - migrates all data into the new schema-bound database
   - reports progress for each step, including bulk copy
   - archives the old readonly database into an `archive/` directory next to the migrated database
   - returns only when the new database is fully ready for normal use

This removes the hot-migration lifecycle entirely.

## Removed Concepts

The redesign removes:

- hot migrations
- cutover
- drain
- write recording
- replay logs
- polling for target readiness
- serving against a database in a temporary `migrating` state
- the `offline` subcommand

The old and new service coordination model is replaced by a single blocking migration flow.

## New API Direction

Primary API:

```fsharp
val startService :
  sqliteDirectory: string ->
  dbFileName: string ->
  schemaIdentity: SchemaIdentity ->
  targetSchema: SqlFile ->
  reportProgress: (string -> Task<unit>) ->
  cancellationToken: CancellationToken ->
  Task<Result<DbTxnBuilder, SqliteException>>
```

A convenience overload can remain that uses a no-op progress callback.

## Runtime Semantics

### New database

- No longer uses `_migration_status` to gate serving.
- Is only returned after migration is complete and the database is ready.
- Keeps `_schema_identity`.

### Old database

- Receives a readonly marker before migration begins.
- Continues to allow reads.
- Rejects writes while marked readonly.
- Keeps that readonly marker after migration.
- Is moved into `archive/` after successful migration.

## CLI Direction

Keep:

- `mig codegen`
- `mig init`
- `mig migrate`

Remove:

- `mig offline`
- `mig drain`
- `mig cutover`
- `mig archive-old`

Likely remove or redesign:

- `mig reset`
- `mig status`

If `mig status` remains, it should report the simplified migration/archive state rather than hot-migration replay state.

## Compiled Project Discovery

The redesign should also simplify how `mig` discovers the compiled schema and generated database module.

Current compiled-mode commands still depend on `--assembly` and `--module`. That is too brittle because callers should not need to know the final assembly output path or the exact compiled module name of the generated `Db.fs`.

Planned improvement:

1. Replace `--assembly` and `--module` with project-based inputs.
2. Make `mig codegen`, `mig init`, and `mig migrate` accept either:
   - `--project <path-to-fsproj>`
   - `--dir <path>` with autodiscovery
3. For `--dir`, inspect only the given directory for runtime `.fsproj` candidates.
4. Exclude `Schema/Schema.fsproj` from runtime project selection.
5. Require exactly one runtime project in the directory or fail clearly.
6. Infer the schema project by convention as `Schema/Schema.fsproj` relative to the runtime project.
7. Resolve the built runtime and schema assemblies automatically from those `.fsproj` files.
8. Add a marker attribute for generated schema modules.
9. Emit that attribute on generated `Db.fs` modules during code generation.
10. Discover the generated schema module by reflection from the compiled runtime assembly instead of requiring the caller to provide a module name.
11. Fail clearly when no generated schema module is found or when multiple generated schema modules are found in the same assembly.

`mig codegen` direction:

1. Given a runtime project or autodiscovered runtime project, locate `Schema/Schema.fsproj`.
2. Resolve the built `Schema.dll` from that schema project.
3. Reflect the compiled schema from that assembly.
4. Generate `Db.fs` into the schema directory.
5. Emit the generated module as `module <TargetProject>.Db`.
6. Use runtime project `<AssemblyName>` when present, otherwise fall back to the runtime project file stem for `<TargetProject>`.
7. Emit the generated schema module marker attribute on the generated module for later discovery by `mig init` and `mig migrate`.

This should support real layouts such as generated modules compiled under names like `SportsKnowledge.Db` and `FSharpKnowledge.Db` without forcing callers to hardcode module names.

## Implementation Plan

1. Replace the `Mig.HotMigration` startup semantics with a blocking migration startup flow.
2. Introduce progress reporting through `startService`.
3. Replace the old recording/draining marker logic with a simpler readonly marker on the old database.
4. Remove `_migration_log`, `_migration_progress`, `_migration_status`, and hot-migration replay behavior.
5. Keep `_schema_identity` in the new database.
6. Add bulk-copy progress reporting so callers can surface table-level and row-level migration updates.
7. Archive the old readonly database into `<sqliteDirectory>/archive/<old-db-file>` after successful migration.
8. Remove `startServiceWithPolling`.
9. Remove `runDrain`, `runCutover`, and related admin/status helpers tied to hot migration.
10. Remove the `offline` CLI subcommand and supporting library flow.
11. Simplify `mig migrate` so it shares the same migration core as `startService`.
12. Update transaction guards so:
    - old database writes are blocked by the readonly marker
    - returned new database handles are always immediately usable
13. Add startup locking so concurrent migration attempts do not race.
14. Rewrite tests to reflect the new readonly blocking migration model.
15. Rewrite docs and specs to match the new design.

## Files Expected To Change

Core library:

- `src/MigLib/HotMigration/Operations/Migration.fs`
- `src/MigLib/HotMigration/SchemaBootstrap.fs`
- `src/MigLib/Db/Recording.fs`
- `src/MigLib/Db/Startup.fs`
- `src/MigLib/HotMigration/Operations/Admin.fs`
- `src/MigLib/HotMigration/Operations/Reporting.fs`
- `src/MigLib/HotMigration/Operations.fs`

CLI:

- `src/mig/Program/Args.fs`
- `src/mig/Program/MigrationCommands.fs`
- `src/MigLib/Build.fs`

Tests:

- `src/Test/Tests.fs`

Docs/specs:

- `README.md`
- `specs/hot_migrations.md`
- `specs/hot_migrations_on_deploy.md`
- `specs/mig_command.md`
- `specs/operator_runbook.md`

## Testing Plan

Add or update tests for:

1. using an existing schema-bound target database
2. initializing a fresh database when no previous database exists
3. migrating from an old database and returning a usable new database
4. rejecting writes on the old database once readonly mode begins
5. continuing to allow reads from the old database while readonly
6. progress callback reporting all expected migration steps
7. bulk-copy progress reporting
8. archiving the old database after successful migration
9. preserving the readonly marker in the archived old database
10. handling archive replacement when an archive file already exists
11. preventing concurrent startup migration races

## Open Decisions Already Settled

- The old database readonly marker should remain in the old database.
- After successful migration, the old database should be moved into an archive directory next to the migrated database.
- The `offline` subcommand is no longer needed and should be removed.

## Summary

The project is moving away from hot migrations toward a simpler and more truthful runtime contract:

- when `startService` returns, the database is ready
- migration happens as a blocking startup operation
- the old database becomes readonly during migration
- the old database is archived after success
- progress is surfaced directly to callers
