# MigLib Workflow Layers

This directory contains the internal workflow layers behind the public `MigLib` facade.

These layers own workflow semantics for operations such as code generation, initialization, planning, migration, status, and reset. The `mig` executable should be a thin adapter over them: it parses arguments, prints progress, formats errors, and returns exit codes.

## Layers

- `Types.fs` defines public workflow contracts shared by direct MigLib callers and the CLI adapter.
- `Resolution/` resolves projects, compiled assemblies, generated schema modules, and database paths.
- `Codegen/` contains implementation details for the code generation workflow.
- `Init/` contains implementation details for creating a fresh schema-bound database.
- `Migrate/` contains implementation details for the blocking migration workflow.
- `Plan/` contains implementation details for dry-run migration planning.

## Dependency Direction

Lower layers must not depend on higher layers.

The intended compile and dependency order is:

1. `Schema/Types.fs`
2. `Types.fs`
3. `Resolution/`
4. workflow implementation directories
5. public `MigLib.fs`

Shared project, assembly, and database discovery belongs in `Resolution/`, not in individual workflow implementations or the CLI.

## Public API

Public contracts live in `Types.fs`, and the public facade functions live in `MigLib.fs`. Implementation modules under this directory should remain `internal` unless a broader surface is deliberately required.
