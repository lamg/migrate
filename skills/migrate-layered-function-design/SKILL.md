---
name: migrate-layered-function-design
description: Use when writing or refactoring F# functions whose body mixes abstraction levels, construction details, or implicit invariants that should be isolated behind named steps.
---

# Migrate Layered Function Design

## Core principle

A function should be readable as one coherent level of reasoning.

When a function interleaves high-level decisions with lower-level construction details, the reader must understand two programs at once: the algorithm being expressed and the subordinate mechanisms used to express it. Refactor so the outer function states the main argument, while helpers discharge lower-level obligations.

This is not extraction for line count. It is abstraction as a correctness tool: each named helper should capture a meaningful value, decision, invariant, or transformation.

## When to apply

- Apply this when a function body mixes orchestration with parsing, SQL construction, reflection, code generation, validation, or other lower-level mechanics.
- Apply this when several bindings exist only to construct one higher-level value used later.
- Apply this when earlier code establishes an invariant that later code silently relies on.
- Apply this when understanding the main function requires mentally executing an unrelated lower-level algorithm.

## Design rule

- Keep the outer function at the highest useful level of abstraction.
- Move lower-level detail into helpers named for what they establish, not how they happen to compute it.
- Prefer the smallest extraction that makes the reasoning boundary clear.
- Do not hide essential control flow, resource lifetime, transactions, or error handling behind vague helper names.
- Do not introduce public API surface for a local reasoning problem.

## Helper quality

A good helper name lets the caller read as if the result had already been specified.

Good helper names describe:

- a domain value: `buildTablePlan`, `inferPrimaryKey`, `generateViewQueries`
- a decision: `requiresDrainReplay`, `canUseFastPath`
- an invariant: `ensureCompatibleSchema`, `validateMigrationOrder`
- a transformation: `normalizeColumnName`, `quoteSqlIdentifier`

Weak helper names merely repeat mechanics:

- `processItems`
- `handleData`
- `doValidation`
- `getStuff`
- `buildResult`

## Refactoring procedure

1. Identify the result the function is trying to establish.
2. Identify bindings that belong to a lower-level concern than that result.
3. Group the lower-level bindings by the value, decision, or invariant they produce.
4. Extract each group only if its name improves the caller.
5. Keep the helper local if it is only part of this function's proof.
6. Move the helper to a private or internal module only when reuse or testing justifies it.
7. Re-read the outer function and check that it now describes the main reasoning path without forcing the reader into implementation detail.

## Example

Avoid mixing the construction of `V` with the lower-level construction of `E` values:

```fsharp
let f () =
    let value0: V = getValue 0
    let exp0: E = getExp 0
    let exp1: E = getExp 1
    let value1: V = joinExps exp0 exp1
    value0, value1
```

Prefer naming the lower-level obligation:

```fsharp
let private getValueFromExps () =
    let exp0: E = getExp 0
    let exp1: E = getExp 1
    joinExps exp0 exp1

let f () =
    let value0: V = getValue 0
    let value1: V = getValueFromExps ()
    value0, value1
```

Here `f` speaks in terms of `V`, the value level relevant to its result. The `E` construction is a subordinate concern and belongs behind a name.

If the helper is only meaningful inside `f`, prefer a local helper:

```fsharp
let f () =
    let getValueFromExps () =
        let exp0: E = getExp 0
        let exp1: E = getExp 1
        joinExps exp0 exp1

    let value0: V = getValue 0
    let value1: V = getValueFromExps ()
    value0, value1
```

## When not to apply

- Do not extract a helper just because a function is long.
- Do not extract a one-line binding unless the name states a real domain concept.
- Do not split code when the caller still needs to know every detail inside the helper.
- Do not create a helper whose name is less precise than the code it hides.
- Do not obscure sequencing, mutation, disposal, transactions, retries, or error propagation.
- Do not add backward-compatibility wrappers unless there is a concrete persisted, public, or external consumer.

## Relation to module layering

This skill is about reasoning inside functions.

If the same lower-level helpers accumulate across a file and form a reusable responsibility layer, also apply `migrate-layered-module-splitting`.

## Checklist

- Does the outer function now read at one abstraction level?
- Does each extracted helper have a name stronger than its implementation?
- Does each helper establish a value, decision, invariant, or transformation?
- Is the helper placed at the narrowest useful scope?
- Are lower-level details hidden without hiding essential behavior?
- Did the refactor preserve the public API unless there was a reason to change it?

## Validation

- Format changed F# files with Fantomas.
- Run `cd src && dotnet test` after code changes.
