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

## What's next

1. Migrate and drain command execution flow

## Completed next-step items

1. Schema diffing and column mapping
    - port/reuse declarative migration engine modules to operate on the same `SqlFile` model
2. Bulk data copy with FK dependency ordering and ID mapping
3. Migration log recording in TaskTxnBuilder
4. Drain replay logic
5. Cutover and status commands
