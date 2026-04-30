---
name: migrate-layered-module-splitting
description: Use when an F# source file or module mixes stacked responsibility layers whose dependency direction should be made explicit through files, modules, and a stable facade.
---

# Migrate Layered Module Splitting

## Core principle

A module split should make the reasoning structure of the program explicit.

Do not split files because they are large. Split them when they contain several different levels of knowledge: primitives, metadata, planning, execution, orchestration, or public API composition. A good split lets the reader understand each layer as a smaller problem with a precise obligation, then understand the top layer as composition of those obligations.

Layering is useful when it reduces the number of facts that must be held in mind at once. Lower layers should solve stable, narrower problems. Higher layers should express policy, workflow, and composition. The dependency direction should follow the direction of reasoning: higher layers may use lower layers, but lower layers must not know the stories told above them.

## What counts as a layer

A layer is not merely a group of functions with similar names. A layer owns a distinct kind of knowledge.

Good layer boundaries often separate:

- primitive types and small operations from workflows that use them
- metadata access from decisions based on metadata
- parsing from interpretation
- planning from execution
- SQL or AST construction from user-facing operations
- reusable internal mechanics from public facade functions

Weak boundaries usually separate:

- functions only because the file is long
- functions only by CRUD verb or alphabetic order
- types from the only code that can make sense of them
- helpers whose callers still need to know all their internals
- mutually dependent code that will immediately create cycles or broad re-exports

## When to apply

- Apply this when a file contains a visible ladder of responsibilities and lower-level code supports higher-level workflows in one direction.
- Apply this when understanding a public function requires scanning unrelated primitives, metadata readers, planners, and execution helpers in the same file.
- Apply this when a set of helpers has become a reusable internal domain with its own vocabulary.
- Apply this when compile order can naturally express the dependency order between the responsibilities.

## Design rule

- Put each responsibility layer in its own source file.
- Keep dependencies one-way: higher layers depend on lower layers, never the reverse.
- Place shared types in the lowest layer that can honestly own them.
- Keep public API functions in the top facade unless there is a concrete reason to expose a lower layer.
- Prefer `internal` modules for lower layers.
- Preserve the existing public API shape when practical; moving implementation should not force callers to learn the new internals.
- Use the split to remove accidental knowledge, not to create more ceremony.

## Splitting procedure

1. Identify the public story the original file tells.
2. Identify the kinds of knowledge mixed into that file.
3. Order those responsibilities from most primitive to most orchestration-oriented.
4. Check that dependencies can flow upward without cycles.
5. Create a directory named after the original area or module.
6. Create one source file per layer, ordered from lower to higher responsibility.
7. Add a top-level facade file or module that preserves the public surface when needed.
8. Update the `.fsproj` compile order to match the layer order.
9. Add a `README.md` that records the responsibilities and dependency direction.
10. Re-read the top layer and verify that it now describes composition instead of implementation mechanics.

## File layout guidance

Prefer names that identify the responsibility, not the extraction event.

Good layer names include:

- `Types`
- `Primitives`
- `Metadata`
- `Parsing`
- `Planning`
- `Execution`
- `Operations`
- `Facade`

Avoid names that describe only mechanics or chronology:

- `Helpers`
- `Utils`
- `Common2`
- `Old`
- `Extracted`
- `Misc`

An idiomatic split often looks like this:

```text
Area/
  README.md
  Types.fs
  Metadata.fs
  Planning.fs
  Execution.fs
  Operations.fs
Area.fs
```

The lowest file should compile first. The public facade should compile after the internal layers it exposes or composes.

## README requirements

Write the `README.md` in English and include:

- the purpose of the directory
- each layer's responsibility
- the dependency direction between layers
- a statement that lower layers must not depend on higher layers
- where the public API lives
- guidance for where future code should be placed

The README is part of the design. It should let a future maintainer place new code without rediscovering the architecture.

## F# project requirements

- Update the `.fsproj` compile order explicitly.
- Compile lower layers before higher layers.
- Keep facade files after the internal files they depend on.
- Prefer top-level module declarations for single-module files.
- Name modules after their directory path, using dotted module names that make ownership explicit, such as `MigLib.Commands.Resolution.Types` for `Commands/Resolution/Types.fs`.
- Prefer `internal` modules for implementation layers unless public exposure is intentional.
- Format changed F# files with Fantomas.

## When not to apply

- Do not split a file merely because it is long.
- Do not split if the proposed files would depend on each other cyclically.
- Do not create a layer whose purpose cannot be stated without saying "helpers" or "misc".
- Do not move a function away from the data or invariant that gives it meaning.
- Do not expose lower-layer modules publicly just to avoid fixing references.
- Do not add compatibility wrappers unless there is a concrete persisted, public, or external consumer.
- Do not perform a module split when `migrate-layered-function-design` would solve the local abstraction problem.

## Relation to function design

Use `migrate-layered-function-design` first when the problem is only inside one function.

Use this skill when the same lower-level obligations have become a file-level responsibility with its own vocabulary, dependency direction, and compile-order consequences.

## Checklist

- Does each layer own a distinct kind of knowledge?
- Can the dependency direction be explained simply and enforced by compile order?
- Are lower layers free of references to higher-level workflows?
- Is the public API still stable unless changing it was intentional?
- Are shared types placed at the lowest honest ownership point?
- Does the README help future changes land in the right layer?
- Did the split reduce the amount of context needed to understand each file?

## Validation

- Format changed F# files with Fantomas.
- Run `cd src && dotnet test` after code changes.
- If references break, fix the layer ownership or add a minimal facade only when justified.
