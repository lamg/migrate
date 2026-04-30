# MigLib Commands

This directory contains the library command layer behind the public `MigLib` facade.

The command layer owns workflow semantics for operations such as code generation, initialization, planning, migration, status, and reset. The `mig` executable should be a thin adapter over this layer: it parses arguments, prints progress, formats errors, and returns exit codes.

## Layers

- `Types.fs` defines public command contracts shared by direct MigLib callers and the CLI adapter.
- `Resolution/` resolves projects, compiled assemblies, generated schema modules, and database paths.
- `Codegen/` contains implementation details for the code generation workflow.
- `Init/` contains implementation details for creating a fresh schema-bound database.
- `Migrate/` contains implementation details for the blocking migration workflow.
- `Plan/` contains implementation details for dry-run migration planning.
- The top-level `Codegen.fs`, `Init.fs`, `Migrate.fs`, and `Plan.fs` files are facades over their implementation directories.

## Dependency Direction

Lower layers must not depend on higher layers.

The intended compile and dependency order is:

1. `Types.fs`
2. `Resolution/`
3. command implementation directories
4. command facade files
5. public `MigLib.fs`

Shared project, assembly, and database discovery belongs in `Resolution/`, not in individual command implementations or the CLI.

## Public API

The public API should live in `MigLib.fs`. Implementation modules under this directory should remain `internal` unless a broader surface is deliberately required.
