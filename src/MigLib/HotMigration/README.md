# HotMigration Layers

This directory splits the former `HotMigration.fs` module into a small set of files with a one-way dependency direction.

The guiding rule is:

- lower layers do not depend on higher layers
- higher layers may compose lower layers
- the public API stays in `Operations.fs` under `Mig.HotMigration`

## Layers

### Primitives

Responsibilities:

- low-level SQLite command creation
- identifier quoting
- conversion between migration expressions and SQLite values
- parsing and rendering small SQL-related value types

This layer should not know about migration workflows.

### Metadata

Responsibilities:

- reading and writing migration bookkeeping tables such as `_migration_status`, `_migration_progress`, and `_schema_identity`
- counting rows and checking table existence

This layer builds on `Primitives` and provides reusable access to internal migration state.

### SchemaBootstrap

Responsibilities:

- rendering schema objects into executable SQL
- preparing the old database for recording
- creating and initializing the new database and migration tables
- performing schema-only initialization

This layer depends on `Primitives` and `Metadata`.

### SchemaIntrospection

Responsibilities:

- reading tables and foreign keys from an existing SQLite database
- reconstructing a `SqlFile` representation from a live database

This layer depends on `Primitives`.

### Copy

Responsibilities:

- projecting rows into the target schema
- inserting copied rows
- maintaining ID mappings
- executing bulk copy plans table by table

This layer depends on `Primitives` and the data-copy domain from `DeclarativeMigrations`.

### Planning

Responsibilities:

- validating target-schema consistency for non-table objects
- summarizing supported and unsupported schema differences
- producing preflight reports and bulk-copy plans

This layer depends on the schema-diff and bulk-copy planning domain.

### Operations

Responsibilities:

- public reports and result types
- high-level workflows such as migrate, init, drain, cutover, reset, archive, and startup service orchestration
- composing all lower layers into user-facing operations

This is the top layer and the only one intended to expose the public `Mig.HotMigration` API.

The operations layer is itself split into a nested directory:

- `Operations/README.md` explains the internal layers of the operations area.
- `Operations/Types.fs` contains the public result and report types.
- `Operations/Shared.fs` contains shared constants and helper values for operation workflows.
- `Operations/Reporting.fs` contains read-only reporting and planning entry points.
- `Operations/Migration.fs` contains migrate, init, startup, and drain workflows.
- `Operations/Admin.fs` contains archive, reset, and cutover workflows.
- `Operations.fs` is a thin facade that re-exports the public API.

## Dependency Direction

The intended dependency direction is:

`Primitives -> Metadata/SchemaBootstrap/SchemaIntrospection/Copy/Planning -> Operations`

More concretely:

- `Metadata` depends on `Primitives`
- `SchemaBootstrap` depends on `Primitives` and `Metadata`
- `SchemaIntrospection` depends on `Primitives`
- `Copy` depends on `Primitives`
- `Planning` depends on declarative migration planning logic and small formatting helpers
- `OperationsReporting`, `OperationsMigration`, and `OperationsAdmin` depend on all lower layers they need
- `Operations` depends on the operation sublayers and acts as the stable public facade

When adding new code, prefer placing it in the lowest layer that can own the responsibility cleanly.
