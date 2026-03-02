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

## Format with Fantomas

- Run Fantomas on changed F# code before committing.
- Format all project F# files with `cd src && fantomas .`.
- Format a specific file with `fantomas src/MigLib/Db.fs`.
