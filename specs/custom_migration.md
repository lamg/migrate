## Custom migrations

This document specifies a future extension point for migrations that cannot be derived only from `Schema.fs`.

It does not change the current v1 rule:

- `Schema.fs` is the source of truth
- `Db.fs` is fully generated
- if a migration cannot be expressed with the currently supported `Schema.fs` primitives, migration planning fails clearly

This document exists to define the later escape hatch when that rule is intentionally expanded.

## Why this exists

Some schema transitions are not safely inferable from structure alone.

Examples:

- splitting one source column into multiple target columns
- merging multiple source columns into one target column
- changing stored semantics, not only names or nullability
- deriving target values from multiple tables
- backfilling values using domain-specific rules
- rewriting data in ways that are correct only for a specific application

Those cases cannot be solved generically by MigLib without additional project-provided logic.

## Goals

- keep `Schema.fs` as the primary source of truth for schema shape
- allow project-specific data transformations when declarative schema metadata is not enough
- keep the extension point explicit, structured, and narrow
- preserve build-time generation vs runtime execution separation
- preserve AOT-friendliness for deployed services
- keep MigLib responsible for orchestration, validation, and bulk migration flow

## Non-goals

- allowing arbitrary handwritten edits inside generated `Db.fs`
- replacing MigLib planning/orchestration with fully custom project migration code
- making every conceivable migration automatically supported
- allowing custom code to bypass MigLib safety checks silently

## File model

The future project file layout becomes:

- `Schema.fs`: declarative schema definition
- `Db.fs`: fully generated query helpers plus generated migration descriptors
- `CustomMigration.fs`: optional handwritten migration helpers for cases that `Schema.fs` alone cannot express

`Db.fs` remains generated-only.

`CustomMigration.fs` is the only handwritten migration-code location.

## When `CustomMigration.fs` is needed

`CustomMigration.fs` is used only when:

- the target schema is valid
- MigLib can still understand the old schema and new schema
- MigLib can identify where custom logic is required
- declarative `Schema.fs` metadata is insufficient to derive the needed data transformation

If a migration is fully expressible with supported `Schema.fs` primitives, no `CustomMigration.fs` file is involved.

## Core model

MigLib remains responsible for:

- determining whether migration is needed
- resolving old and new database paths
- reading old schema and target schema
- building the migration plan
- deciding which parts are automatic and which require custom transforms
- creating the target schema
- running the overall `migrate -> drain -> cutover` flow
- validating that required custom migration hooks exist
- failing clearly when the migration contract is incomplete

`CustomMigration.fs` is responsible only for the application-specific data transformations that MigLib cannot derive safely.

## How custom migrations are referenced

Future `Schema.fs` primitives should declare custom migration intent explicitly.

Examples of future intent, conceptually:

- a target column requires a named transform
- a target table requires a custom backfill step
- a rename is not enough because the data shape also changes
- a multi-table derivation is needed before target rows can be written

The important rule is:

- `Schema.fs` declares that custom migration behavior is required
- `CustomMigration.fs` provides the implementation for that declared behavior

MigLib should not discover custom logic by convention alone without corresponding intent in `Schema.fs`.

## Build-time contract

At build time:

- MigLib reads `Schema.fs`
- MigLib generates `Db.fs`
- MigLib inspects whether `Schema.fs` declares any custom migration hooks
- if custom hooks are declared, MigLib validates that matching implementations exist in `CustomMigration.fs`
- generated migration descriptors in `Db.fs` include references to those validated custom hooks

Build-time validation should fail if:

- `Schema.fs` declares a custom migration hook that does not exist
- multiple hooks with the same identity exist
- a hook has an invalid signature
- a hook is declared for an unsupported migration stage

## Runtime contract

At runtime:

- services use compiled `Schema.fs`, generated `Db.fs`, and optional compiled `CustomMigration.fs`
- runtime execution does not inspect source code
- MigLib executes the migration plan using compiled descriptors
- when the plan reaches a declared custom migration step, MigLib invokes the validated compiled hook

Runtime should not need to discover or compile anything.

## Shape of the extension point

The extension point should be narrow and stage-based.

Possible future stages:

- row transform during bulk copy
- table backfill after target table creation
- pre-cutover data fixup

The preferred design is not “run arbitrary code whenever you want”.

The preferred design is:

- MigLib defines a small number of well-defined extension stages
- each stage has a fixed contract
- `CustomMigration.fs` provides implementations only for those contracts

## Preferred safety properties

Custom migration hooks should:

- run inside MigLib-controlled transactions when possible
- receive typed or validated inputs, not raw ambient global state
- be deterministic relative to input database state
- make their side effects explicit
- return structured success/failure results

MigLib should continue to own:

- transaction boundaries
- replay/cutover coordination
- marker/status tables
- schema identity handling
- failure reporting

## Failure behavior

Migration planning or startup should fail clearly when:

- a custom migration hook is required but missing
- a hook throws or returns failure
- a hook produces data that violates the target schema
- a hook is declared in a place MigLib cannot safely order

Custom migration support is an explicit extension of MigLib, not a bypass around it.

## Compatibility with the current plan

For now, the implementation should continue to assume:

- no `CustomMigration.fs`
- no handwritten migration code
- only migrations supported by current `Schema.fs` primitives are allowed

That keeps the current scope narrow.

This document only reserves the shape of the future extension point so today’s design does not accidentally block it.

## Open questions

1. How should `Schema.fs` declare a custom migration hook identity: attribute name, DU case, or another typed descriptor?
2. Should hooks be row-oriented, set-oriented, or both?
3. What should the public signature of a hook be so that it is expressive enough without letting projects bypass MigLib orchestration too easily?
4. Which migration stages should be hookable in v1 of `CustomMigration.fs`, and which should remain intentionally unsupported?
