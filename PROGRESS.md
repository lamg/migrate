# Progress

## Fresh start (2026-02-17)

Deleted old project (src/, spec.md, PROGRESS.md) and created new skeleton based on specs in `specs/`:

- `specs/database_dsl.md` — F# DSL where types + attributes define database schema
- `specs/hot_migrations.md` — three-phase hot migration strategy (Migrate → Drain → Cutover)
- `specs/mig_command.md` — CLI commands: `migrate`, `drain`, `cutover`, `status`

## Current structure

```
src/
├── Directory.Packages.props   (central package management)
├── migrate.slnx               (solution with 3 projects)
├── MigLib/
│   ├── MigLib.fsproj
│   └── Db.fs                  (TaskTxnBuilder CE + attribute types)
├── mig/
│   ├── mig.fsproj
│   └── Program.fs             (Argu CLI: migrate, drain, cutover, status)
└── Test/
    ├── Test.fsproj
    └── Tests.fs               (placeholder xunit test)
```

## What's implemented

- **MigLib/Db.fs**: All DSL attribute types (AutoIncPK, PK, Unique, Default, DefaultExpr, Index, SelectAll, SelectBy, SelectOneBy, SelectLike, SelectByOrInsert, UpdateBy, DeleteBy, InsertOrIgnore, OnDeleteCascade, OnDeleteSetNull, View, Join, ViewSql, OrderBy) and TaskTxnBuilder CE skeleton (Run, Zero, Return, Bind, Combine, Delay, For)
- **mig/Program.fs**: Argu CLI with MigrateArgs, DrainArgs, CutoverArgs, StatusArgs and stub dispatch functions
- **Test/Tests.fs**: Single placeholder test

## Update (2026-02-17)

- **Schema model restored**: `MigLib/DeclarativeMigrations/Types.fs` has the reusable intermediate model (`SqlFile`, `CreateTable`, annotations, normalized-table helpers) that both migration and query generation can share.
- **Code generation stack restored**: `MigLib/CodeGen/*` was ported from `master` (TypeGenerator, QueryGenerator, Normalized* generators, Fantomas/Fabulous helpers, ProjectGenerator).
- **Input/output boundary split**: New `MigLib.CodeGen.CodeGen.generateCodeFromModel` generates F# query code from an in-memory schema model. This keeps codegen reusable while switching input from SQL parsing to `.fsx` reflection.
- **Type reflection implemented**: `MigLib/SchemaReflection.fs` maps attributed F# records/unions/views into `SqlFile` (PK/FK/defaults/unique/index/query annotations, `ViewSql`, and DU extension tables).
- **Reflection-to-codegen bridge added**: `generateCodeFromTypes` now runs reflection + query generation in one call.
- **CPM project generation preserved**: Generated `.fsproj` files still emit package references without inline versions (`FsToolkit.ErrorHandling`, `Microsoft.Data.Sqlite`, `MigLib`).
- **Tests expanded**: Added tests for model-driven code generation, reflection mapping, reflection-driven codegen, and CPM project output.

## Update (2026-02-17, later)

- **`.fsx` schema execution added**: `MigLib/SchemaScript.fs` now evaluates database scripts with FSI and feeds reflected record/union types into `SchemaReflection`.
- **Seed extraction from script values implemented**: module-level `let` bindings are read from generated FSI module properties/fields and converted into `SqlFile.inserts` with FK-aware dependency ordering.
- **View join SQL synthesis completed**: `[<View>]` + `[<Join>]` now synthesizes `CREATE VIEW` SQL from reflected table metadata, including inferred FK join conditions and field-to-column projection resolution.
- **Script/codegen bridge completed**: `generateCodeFromScript` now supports full `.fsx` -> schema model -> generated query module flow.
- **Validation passed**: `fantomas .`, `dotnet test`, and `dotnet build mig/mig.fsproj` all succeed.

## Update (2026-02-17, schema diffing)

- **Schema diff module added**: `MigLib/DeclarativeMigrations/SchemaDiff.fs` now computes table-level schema diffs (`addedTables`, `removedTables`, `renamedTables`, `matchedTables`) on the shared `SqlFile` model.
- **Column mapping added**: the same module derives per-table copy mappings with source strategies (`SourceColumn`, `DefaultExpr`, `TypeDefault`) to support copy-time projection for evolved schemas.
- **Schema copy plan added**: `buildSchemaCopyPlan` combines schema diff + table mappings in target-table order.
- **Coverage added**: tests now validate rename detection and column-mapping behavior, including renamed columns and default/type-default fill for new columns.

## Update (2026-02-17, bulk data copy planning)

- **Bulk copy planner added**: `MigLib/DeclarativeMigrations/DataCopy.fs` introduces `buildBulkCopyPlan`, producing FK-aware copy steps ordered by table dependencies.
- **ID mapping flow added**: the module adds row projection and mapping helpers (`projectRowForInsert`, `recordIdMapping`) plus an in-memory ID mapping store for old->new key translation.
- **FK translation added**: projected target rows now translate foreign-key values via ID mappings before insert payload generation.
- **Coverage added**: tests validate parent-before-child copy order, FK remapping through stored ID mappings, and clear error reporting when FK mappings are missing.

## Update (2026-02-17, migration log recording)

- **TaskTxn migration mode support added**: `TaskTxnBuilder` now detects `_migration_marker` status (`recording` / `draining`) per transaction.
- **Write gating added**: `MigrationLog.ensureWriteAllowed` blocks writes in drain mode with a `SqliteException`.
- **Buffered log flush added**: write events are buffered in transaction context and flushed to `_migration_log` on successful commit, preserving `ordering` and shared `txn_id`.
- **Generated write-method hooks added**: regular query generator methods now emit `MigrationLog.ensureWriteAllowed` and `MigrationLog.recordInsert|Update|Delete` calls; normalized write methods now emit `MigrationLog.ensureWriteAllowed`.
- **Coverage added**: tests validate recording flush, drain-mode rejection, no-marker no-op behavior, and codegen hook emission.

## Update (2026-02-17, drain replay logic)

- **Drain replay module added**: `MigLib/DeclarativeMigrations/DrainReplay.fs` now reads `_migration_log` entries, groups operations by transaction, and replays insert/update/delete actions using the bulk-copy mapping plan.
- **Replay identity translation added**: replay paths reuse ID mapping translation from `DataCopy`, including FK remapping and source->target identity lookup for updates/deletes.
- **Persistent ID mapping updates added**: successful replay inserts now upsert `_id_mapping` entries for single-column identities to support subsequent dependent operations.
- **Replay transactional behavior added**: each source transaction group is replayed inside its own SQLite transaction, committing as a unit and rolling back on failure.
- **Coverage added**: tests validate transaction grouping/order, migration log + mapping loading, end-to-end insert/update/delete replay with ID translation, and rollback on replay failure.

## Update (2026-02-17, cutover and status commands)

- **Hot migration command module added**: `MigLib/HotMigration.fs` now implements reusable database operations for migration `status` inspection and `cutover`.
- **Status reporting implemented**: status now reads old DB marker/log counts and, when a new DB is provided, reads `_migration_status` and `_id_mapping` counts with pending replay count reporting.
- **Cutover execution implemented**: cutover now validates `_migration_status`, drops `_id_mapping` if present, and sets `_migration_status(id=0)` to `ready` in one transaction.
- **CLI wiring completed**: `mig status` and `mig cutover` now call MigLib implementations and return non-zero exit codes on failure with clear error output.
- **Coverage added**: tests validate status snapshots (with/without migration tables), successful cutover state transition and cleanup, and cutover failure when `_migration_status` is missing.

## Update (2026-02-17, migrate and drain execution flow)

- **Migrate execution implemented**: `MigLib.HotMigration.runMigrate` now evaluates the `.fsx` schema, introspects old DB schema, builds bulk-copy plans, prepares old DB recording markers/log table, initializes the new DB schema/migration tables, and copies data with ID mapping persistence.
- **Drain execution implemented**: `MigLib.HotMigration.runDrain` now switches old DB marker to `draining`, loads bulk-copy/replay metadata from both databases, and replays migration log entries into the new DB.
- **SQLite schema bridge added**: hot migration now includes runtime SQLite schema introspection + SQL DDL rendering so migration planning/copy can operate from live databases and reflected schema models.
- **CLI wiring completed**: `mig migrate` and `mig drain` now execute real flows and print structured progress summaries with non-zero exit codes on failure.
- **Coverage added**: end-to-end tests now validate migrate setup/copy behavior and drain replay/consumption behavior against real SQLite files.

## Update (2026-02-17, migration safety hardening)

- **Replay checkpoint table added**: new DB migrations now create `_migration_progress(id=0, last_replayed_log_id, drain_completed)` to persist drain progress.
- **Exact pending replay accounting added**: `status --new` now reports pending replay as `_migration_log.id > last_replayed_log_id` instead of mirroring total log row count.
- **Drain progress persistence added**: drain updates `_migration_progress` during replay and marks `drain_completed=1` only when no pending entries remain.
- **Stricter cutover prechecks added**: cutover now requires `_migration_progress` to exist with `drain_completed=1` before switching `_migration_status` to `ready`.
- **Coverage added**: tests now validate pending replay checkpoint math, cutover rejection before drain completion, migrate initialization of checkpoint state, and drain checkpoint progression.

## Update (2026-02-18, operational cleanup policy)

- **Cutover cleanup expanded**: `runCutover` now drops both replay-only tables in the new DB (`_id_mapping`, `_migration_progress`) before setting `_migration_status` to `ready`.
- **Cutover idempotency improved**: rerunning cutover while already `ready` now succeeds even after replay tables were removed.
- **Status reporting hardened post-cutover**: `getStatus` now reports table presence (`_id_mapping`, `_migration_progress`) and treats pending replay as `0` when the new DB is `ready`.
- **CLI status output clarified**: `mig status --new` now prints removed/present migration-table state after cutover instead of only raw counts.
- **Coverage added**: tests now validate post-cutover status output semantics, replay-table cleanup on cutover, and idempotent ready-state cutover.

## Update (2026-02-18, old database cleanup command)

- **Old-db cleanup flow added**: `MigLib.HotMigration.runCleanupOld` now removes `_migration_marker` and `_migration_log` in one transaction for archived old databases.
- **Safety guard added**: cleanup fails while the old marker status is still `recording` to prevent deleting active migration state mid-flight.
- **CLI command added**: `mig cleanup-old --old <path>` now reports previous marker status plus whether each migration table was dropped.
- **Coverage added**: tests now validate successful cleanup, idempotent no-op behavior when tables are already absent, and guarded failure in recording mode.

## Update (2026-02-18, CLI integration coverage)

- **CLI integration harness added**: test project now references `mig` and runs real CLI parsing/dispatch through `Mig.Program.main`.
- **Status output path covered**: integration test verifies `mig status --new` output semantics after cutover cleanup (`pending replay` annotation and removed-table reporting).
- **Cutover error path covered**: integration test verifies `mig cutover` non-zero exit + stderr when drain is incomplete.
- **Cleanup-old output/error paths covered**: integration tests verify both successful cleanup summaries and guarded failure while marker status is `recording`.
- **CLI command naming hardened**: `cleanup-old` subcommand now uses explicit command-line naming (hyphenated) via `CustomCommandLine`.

## Update (2026-02-18, README refresh)

- **Root docs aligned with current CLI**: `README.md` now documents the active command set (`migrate`, `drain`, `cutover`, `cleanup-old`, `status`) and removes legacy command references.
- **Operational quickstart updated**: README quickstart now shows the online hot-migration flow used by the implemented toolchain.
- **Spec links clarified**: README now points directly to `specs/database_dsl.md`, `specs/hot_migrations.md`, and `specs/mig_command.md`.

## Update (2026-02-18, operator runbook)

- **Runbook added**: new `specs/operator_runbook.md` documents end-to-end hot migration operations with preflight checks, phase-by-phase validation, and common failure triage.
- **Rollback notes added**: runbook now captures practical rollback guidance before and after cutover/cleanup to reduce operator ambiguity.
- **README linked**: root README now includes the runbook in the specs section.

## Update (2026-02-18, deterministic migrate pathing)

- **Current-directory deterministic pathing added**: `mig migrate` now defaults target naming to `./<dir-name>-<schema-hash>.sqlite`.
- **No-arg auto-discovery added**: `mig migrate` now defaults `schema` to `./schema.fsx` and auto-detects source DB as exactly one `./<dir-name>-<old-hash>.sqlite` file excluding the target.
- **Schema-hash match handling added**: when the schema-matched target database already exists and no source candidate is found, migrate exits as a no-op (`Migrate skipped.`).
- **Coverage added**: CLI integration tests now validate deterministic current-directory pathing, no-arg auto-discovery flow, and schema-matched no-op behavior.
- **Docs aligned**: README/specs/runbook now describe current-directory deterministic behavior for `mig migrate`.

## What's next

1. Add CLI integration coverage for argument-parser help/usage output (root `--help` and subcommand `--help`).

## Completed next-step items

1. Schema diffing and column mapping
    - port/reuse declarative migration engine modules to operate on the same `SqlFile` model
2. Bulk data copy with FK dependency ordering and ID mapping
3. Migration log recording in TaskTxnBuilder
4. Drain replay logic
5. Cutover and status commands
6. Migrate and drain command execution flow
7. Migration safety hardening
8. Operational cleanup policy (migration table retention/removal and status output after cutover)
9. Optional old-database cleanup command for archived environments
10. CLI integration tests for `mig` output/error paths (status, cutover, cleanup-old)
11. Refresh root README to current command surface
12. End-to-end operator runbook with preflight/rollback notes
13. Deterministic default new DB path from schema hash
