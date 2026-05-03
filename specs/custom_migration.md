# Custom migrations

This document reserves a future extension point for schema transitions that cannot be derived safely from `MigSchema.fs` alone.

## Current rule

Today:

- `MigSchema.fs` is the source of truth for schema shape
- `Db.fs` is fully generated
- if a migration cannot be expressed with the supported schema primitives, planning fails clearly

There is no current handwritten migration hook.

## Why an extension point may be needed

Some transitions are inherently application-specific:

- splitting one source column into multiple target columns
- merging multiple source columns into one target column
- deriving target values from several source tables
- rewriting values according to business rules
- backfilling target rows that cannot be inferred from structure alone

## Desired shape

A future extension should keep MigLib in control of:

- project and database discovery
- migration planning
- target database creation
- bulk copy orchestration
- validation and failure reporting

Project code should only provide the transformation logic that MigLib cannot infer safely.

## File model

If this is implemented later, the intended file model is:

- `MigSchema.fs`: declarative schema definition
- `Db.fs`: fully generated runtime surface
- `CustomMigration.fs`: optional handwritten migration helpers

`Db.fs` should remain generated-only.

## Likely extension stages

Reasonable future hooks include:

- row transformation during bulk copy
- table-level backfill after target table creation
- post-copy validation or fixup before the old database is archived

The goal is a narrow, stage-based contract, not an unrestricted “run arbitrary code whenever you want” API.

## Current status

For now, the implementation should continue to assume:

- no `CustomMigration.fs`
- no handwritten migration code
- only migrations supported by current `MigSchema.fs` primitives are allowed
