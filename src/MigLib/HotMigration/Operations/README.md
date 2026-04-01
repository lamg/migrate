# Operations Layers

This directory contains the internal sublayers that make up the `Mig.HotMigration` operations layer.

`../Operations.fs` remains the public facade. The files in this directory provide the implementation behind that facade.

## Layers

### Types

Responsibilities:

- define the public report and result record types used by hot migration operations
- keep those types in one place so all operation sublayers share the same shapes

This file contains data shapes only.

### Shared

Responsibilities:

- define small shared constants and helpers used by multiple operation workflows
- centralize operation-specific shared values such as migration table names and common reset messages

This file should stay small and dependency-light.

### Reporting

Responsibilities:

- implement read-only reporting entry points
- build migrate plans, status reports, and preflight summaries

This layer depends on introspection, metadata, planning, and shared operation types.

### Migration

Responsibilities:

- implement migrate, init, startup, and drain workflows
- orchestrate schema loading, initialization, bulk copy, and replay progression

This layer depends on lower hot migration layers and shared operation types.

### Admin

Responsibilities:

- implement archive, reset, and cutover workflows
- enforce safety checks around destructive or state-transitioning operations

This layer depends on metadata access, shared helpers, and operation result types.

## Dependency Direction

The intended direction inside this directory is:

`Types -> Shared -> Reporting/Migration/Admin`

And then:

`Operations/* -> ../Operations.fs`

The public `Mig.HotMigration` module should remain in `../Operations.fs`, which re-exports the stable API surface while these files carry the implementation details.
