# NormalizedQueryGenerator Layers

This directory contains the internal layers behind the `Mig.CodeGen.NormalizedQueryGenerator` facade.

`../NormalizedQueryGenerator.fs` remains the stable facade and re-exports `generateNormalizedTableCode`. The implementation is split here by responsibility.

## Layers

### Common

Responsibilities:

- provide shared helpers for SQL snippets, field-pattern generation, parameter binding, primary-key inspection, join rendering, and validation helpers
- expose reusable utilities for all normalized query code generation paths

### InsertSelect

Responsibilities:

- generate insert and insert-or-ignore members for normalized DU-backed tables
- generate select-all, select-by-id, and select-one members

This layer depends on `Common`.

### UpdateDelete

Responsibilities:

- generate update and delete members for normalized tables and extensions
- own the AST-backed delete generation and SQL update/delete composition

This layer depends on `Common`.

### QueryExtensions

Responsibilities:

- generate custom `QueryBy`, `QueryLike`, and `QueryByOrCreate` members
- validate and extract query values across normalized DU cases

This layer depends on `Common`.

### Generate

Responsibilities:

- orchestrate validation and combine all generated member strings into the final formatted type extension

This is the top layer in this area.

## Dependency Direction

The intended direction is:

`Common -> InsertSelect/UpdateDelete/QueryExtensions -> Generate -> ../NormalizedQueryGenerator.fs`

Lower layers should not depend on higher layers. Keep new code in the lowest layer that can own the responsibility cleanly.
