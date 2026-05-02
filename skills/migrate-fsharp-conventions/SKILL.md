---
name: migrate-fsharp-conventions
description: F# style rules for the migrate project. Use when editing or reviewing `.fs`/`.fsi` files to enforce single-argument call syntax, module-per-file declarations, and Fantomas formatting requirements.
---

# Migrate F# Conventions

## Use single-argument function syntax

- Prefer function-call style for single-argument methods and functions.
- Write `upper.Contains "NOT NULL"`, `str.StartsWith "CREATE"`, and `Default <| Value ""`.
- Avoid parenthesized single-argument calls such as `upper.Contains("NOT NULL")`.

## Use module-per-file declarations

- Prefer top-level module declarations for single-module files.
- Write `module migrate.Db` at file top.
- Avoid `namespace ...` with nested `module Db =` for single-module files.

## Use taskResult for Task<Result<_, _>> workflows

- Prefer `taskResult` for functions returning `Task<Result<'a, 'e>>`.
- Use `let!` directly for `Result<'a, 'e>` and `Task<Result<'a, 'e>>` values so errors short-circuit through the computation expression.
- Use `do!` directly for plain `Task<unit>` values inside `taskResult`.
- Add explicit type annotations on `let!` bindings when F# cannot disambiguate `Task<'a>` from `Task<Result<'a, 'e>>` builder overloads.
- Avoid manually matching on `Result` inside `task { ... }` when `taskResult { ... }` can express the same control flow.

## Format with Fantomas

- Run Fantomas on changed F# code before committing.
- Format all project F# files with `cd src && fantomas .`.
- Format a specific file with `fantomas src/MigLib/Db.fs`.
