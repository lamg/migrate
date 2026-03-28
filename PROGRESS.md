Roadmap for [hot_migrations_on_deploy.md](/var/home/lamg/Documents/migrate/specs/hot_migrations_on_deploy.md)

## 1. Make `Schema.fs` the real source of truth

Status: complete

Completed:

- [x] `MigLib` owns reusable code generation and migration/runtime functionality
- [x] generated `Db.fs` carries compiled `Schema`, `SchemaHash`, `SchemaIdentity`, and `DbFile`
- [x] `mig` runtime/codegen flows use compiled assemblies/modules
- [x] `.fsx` support is removed from runtime, codegen, and tests

## 2. Define declarative schema metadata for generated migrations

Status: deferred for now

Completed:

- [x] `PreviousNameAttribute` for explicit table and column renames
- [x] `DropColumnAttribute` for explicit source-column drops on surviving tables
- [x] heuristic rename inference removed
- [x] automatic mapping restricted to clearly safe cases only
- [x] unsupported data-losing transitions fail clearly during planning

Remaining:

- [ ] define the next declarative metadata primitives beyond `PreviousName` and `DropColumn`
- [ ] decide how supported non-trivial transformations are expressed in `Schema.fs`
- [ ] keep unsupported transitions failing clearly at planning/generation time

## 3. Simplify `mig` around the new model

Status: complete

Completed:

- [x] compiled-module mode for runtime commands
- [x] compiled-module mode for path-only commands
- [x] compiled-schema mode for `mig codegen`
- [x] CLI/help/docs aligned with generated `Db.fs` defaults

Remaining:

- [x] simplify [Program.fs](/var/home/lamg/Documents/migrate/src/mig/Program.fs) now that runtime and codegen are compiled-only
- [x] reconcile any remaining docs/specs that still describe the old script-based model

## Current next task

- no active implementation task in this roadmap
- item 2 remains deferred until we decide to add more declarative migration metadata

This file is the reference for current progress and remaining work on this branch.
