# Progress

## Direction

Reimplement MigLib around a simpler blocking migration process exposed through the new `src/MigLib/Commands/` layer.

The public contract should be truthful and easy to reason about:

- when a command returns success, its operation is complete
- when `startService` returns, the database is ready for normal use
- migration happens before serving, not concurrently with serving
- old databases become readonly before data is copied
- successful migrations archive the old readonly database
- command workflows are available to both direct MigLib users and the `mig` CLI

This replaces the current hot-migration lifecycle with a blocking startup and command model.

## Core Architecture

MigLib should own command semantics. The `mig` executable should become a thin adapter.

Responsibilities:

- `src/MigLib/Commands/` owns workflow correctness for `codegen`, `init`, `migrate`, `plan`, and any remaining `status` or `reset` workflows.
- `src/MigLib/MigLib.fs` exposes the stable public facade for library users.
- `src/mig/` parses CLI arguments, prints progress and reports, maps command results to exit codes, and delegates to MigLib.

The command layer is not a mechanical move of current CLI code into the library. It is a reimplementation of the operational API using the existing codebase only where the existing code still matches the new model.

## Commands Layer Structure

Current intended structure:

```text
src/MigLib/Commands/
  README.md
  Types.fs

  Resolution/
    Types.fs
    Projects.fs
    Assemblies.fs
    GeneratedSchema.fs
    DatabasePaths.fs
  Resolution.fs

  Codegen/
    Inputs.fs
    Execution.fs
  Codegen.fs

  Init/
    SchemaInit.fs
    Execution.fs
  Init.fs

  Migrate/
    Discovery.fs
    Planning.fs
    Execution.fs
    Archive.fs
  Migrate.fs

  Plan/
    Discovery.fs
    Reporting.fs
  Plan.fs
```

Layer responsibilities:

- `Commands/Types.fs` defines shared public command contracts such as `MigProject`, `MigError`, command results, and progress callbacks.
- `Commands/Resolution/` resolves runtime projects, schema projects, compiled assemblies, generated schema modules, database paths, and archive paths.
- `Commands/Codegen/` owns the project-based code generation workflow.
- `Commands/Init/` owns fresh database creation from a compiled schema.
- `Commands/Migrate/` owns the blocking migration workflow.
- `Commands/Plan/` owns dry-run planning and reports.
- The top-level command files are facades over their implementation directories.
- `MigLib.fs` is the public facade over the command layer.

Module names should follow directory ownership with dotted names, for example `MigLib.Commands.Resolution.Types` for `Commands/Resolution/Types.fs`.

## Blocking Migration Semantics

The new migration behavior is:

1. Resolve the runtime project, schema project, compiled schema module, target database path, and possible source database path.
2. If the schema-bound target database already exists and is usable, return it immediately.
3. If no target database exists and no source database exists, initialize a fresh schema-bound database.
4. If a source database exists:
   - acquire startup/migration ownership so concurrent migrations do not race
   - mark the source database readonly before data copy begins
   - create the new schema-bound target database
   - copy applicable data into the new database
   - report progress at command-level and bulk-copy milestones
   - archive the old readonly database into an `archive/` directory next to the database directory
   - return only after the target database is ready for normal use

The old database may continue to allow reads while readonly, but writes must be rejected after the readonly marker is set.

## Existing Code Reuse Policy

Bring forward existing implementation code only when it still fits the blocking command model.

Applicable sources include:

- code generation primitives from `MigLib.Build` and `Mig.CodeGen.*`
- schema reflection and compiled schema loading from `MigLib.CompiledSchema`
- declarative schema diffing and copy planning from `MigLib.DeclarativeMigrations.*`
- SQLite helpers and transaction utilities from `MigLib.Db.*`
- selected migration planning and copy ideas from the old hot-migration implementation

Do not preserve old code merely for compatibility with the hot-migration lifecycle. In particular, avoid carrying forward concepts that only exist to support serving during migration, replay, drain, or cutover.

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
- `_migration_log`
- `_migration_progress`
- `_migration_status`

The old and new service coordination model is replaced by a single blocking migration flow.

## Public API Direction

Command facade direction:

```fsharp
type MigProject = Commands.Types.MigProject

val codegen : MigProject -> Result<CodegenResult, MigError>
val init : MigProject -> Task<Result<InitResult, MigError>>
val migrate : ProgReport -> MigProject -> Task<Result<MigrateResult, MigError>>
val plan : MigProject -> Task<Result<PlanResult, MigError>>
```

`status` and `reset` remain open decisions. If kept, they should describe or manipulate the simplified archive/blocking-migration state, not hot-migration state.

## CLI Direction

Keep:

- `mig codegen`
- `mig init`
- `mig migrate`
- `mig plan`

Remove:

- `mig offline`
- `mig drain`
- `mig cutover`
- `mig archive-old`

Likely remove or redesign:

- `mig reset`
- `mig status`

The CLI should delegate to `MigLib` command functions and should not own project discovery, assembly resolution, migration semantics, or database-copy behavior.

## Project Discovery Direction

Commands should accept project-based inputs rather than requiring callers to know final assembly paths and generated module names.

Planned behavior:

1. Accept `--project <path-to-fsproj>` or a directory with autodiscovery.
2. For directory autodiscovery, inspect only the given directory for runtime `.fsproj` candidates.
3. Exclude `MigSchema/MigSchema.fsproj` from runtime project selection.
4. Require exactly one runtime project or fail clearly.
5. Infer the schema project by convention as `MigSchema/MigSchema.fsproj` relative to the runtime project.
6. Resolve built runtime and schema assemblies automatically from those `.fsproj` files.
7. Emit a marker attribute on generated `Db.fs` modules during code generation.
8. Discover the generated schema module by reflection from the compiled runtime assembly.
9. Fail clearly when no generated schema module is found or when multiple generated schema modules are found.

This should support real generated module names such as `SportsKnowledge.Db` and `FSharpKnowledge.Db` without forcing users to hardcode module names.

## Implementation Plan

1. Finish the `Commands` skeleton and keep lower implementation modules internal.
2. Define stable contracts in `Commands/Types.fs`.
3. Implement `Commands.Resolution` as the shared project, assembly, schema module, and database-path resolver.
4. Reimplement `Commands.Codegen` using applicable codegen primitives.
5. Reimplement `Commands.Init` using applicable schema creation and seed insertion logic.
6. Reimplement `Commands.Plan` using applicable declarative diff and planning logic.
7. Reimplement `Commands.Migrate` as the blocking migration workflow.
8. Rework `startService` to use the same blocking migration core where practical.
9. Remove or replace hot-migration-only library APIs.
10. Thin the `mig` CLI so subcommands delegate to `MigLib` command functions.
11. Rewrite tests around the command layer and blocking migration semantics.
12. Rewrite docs/specs to describe the new model.

## Files Expected To Change

Public command layer:

- `src/MigLib/Commands/README.md`
- `src/MigLib/Commands/Types.fs`
- `src/MigLib/Commands/Resolution/*`
- `src/MigLib/Commands/Codegen/*`
- `src/MigLib/Commands/Init/*`
- `src/MigLib/Commands/Migrate/*`
- `src/MigLib/Commands/Plan/*`
- `src/MigLib/MigLib.fs`
- `src/MigLib/MigLib.fsproj`

Likely supporting library areas:

- `src/MigLib/Build.fs`
- `src/MigLib/CompiledSchema.fs`
- `src/MigLib/Db/*`
- `src/MigLib/DeclarativeMigrations/*`
- `src/MigLib/HotMigration/*`, mostly to remove or harvest applicable pieces before deletion/renaming

CLI adapter:

- `src/mig/Program.fs`
- `src/mig/Program/Common.fs`
- `src/mig/Program/Resolution.fs`, likely removed or reduced
- `src/mig/Program/BuildCommands.fs`
- `src/mig/Program/MigrationCommands.fs`
- `src/mig/mig.fsproj`

Tests and docs:

- `src/Test/Tests.fs`
- `README.md`
- `specs/hot_migrations.md`, likely replaced or archived
- `specs/mig_command.md`
- `specs/operator_runbook.md`

## Testing Plan

Add or update tests for:

1. resolving a runtime project and schema project by convention
2. failing clearly when project discovery is ambiguous
3. generating `Db.fs` into the schema directory
4. initializing a fresh schema-bound database
5. using an existing schema-bound target database
6. migrating from an old database and returning a usable new database
7. rejecting writes on the old database once readonly mode begins
8. continuing to allow reads from the old database while readonly
9. progress callback reporting expected migration steps
10. bulk-copy progress reporting
11. archiving the old database after successful migration
12. preserving the readonly marker in the archived old database
13. preventing concurrent startup migration races
14. CLI commands delegating to MigLib command functions rather than duplicating semantics

## Open Decisions

- Whether `status` remains as a public command.
- Whether `reset` remains as a public command.
- Whether hot-migration namespaces should be renamed as applicable pieces move into the blocking model.
- The exact result shape for `plan`, `status`, and `reset`.

## Settled Decisions

- Migration is blocking, not hot.
- `mig` should not own migration semantics.
- `Commands` is the library workflow layer.
- Existing code should be harvested selectively, not preserved wholesale.
- The old database readonly marker should remain in the old database.
- After successful migration, the old database should be moved into an archive directory next to the migrated database.
- The `offline`, `drain`, `cutover`, and `archive-old` subcommands are no longer part of the target CLI model.
