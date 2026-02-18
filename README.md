[![logo][logo]][migtool]

[![.NET][dotnet-badge]](https://dotnet.microsoft.com/)
[![F#][fs-badge]](https://fsharp.org/)
[![License: Apache2][apache-badge]][apache2]
[![NuGet Version][nuget-version]][migtool]
[![NuGet Downloads][nuget-downloads]][migtool]
![Tests][tests]

Migrate is a SQLite-first migration toolchain built around F# schema scripts (`.fsx`).
It provides a hot-migration workflow (`migrate` -> `drain` -> `cutover`) plus type-safe code generation from reflected schema types.

## Installation

If you just want to test the tool without installing [.Net][dotnet],
then you can use a Docker image:

```sh
podman run -it 'mcr.microsoft.com/dotnet/sdk:10.0' bash
```

Inside the container run:

```sh
export PATH="$PATH:/root/.dotnet/tools"
```

After having [.Net][dotnet] in your system you can run

```sh
dotnet tool install --global migtool
```

## Quickstart (Online Migration)

Assuming:

- an existing SQLite database at `old.db`
- a target schema script at `schema.fsx`

```sh
# from your project directory:
# - expects ./schema.fsx
# - expects exactly one source db matching <dir>-<old-hash>.sqlite
# - derives target db as <dir>-<schema-hash>.sqlite
mig migrate

# then continue with explicit paths printed by migrate output
mig status --old old.sqlite --new project-<schema-hash>.sqlite
mig drain --old old.sqlite --new project-<schema-hash>.sqlite
mig cutover --new project-<schema-hash>.sqlite
# optional, after traffic has fully moved to the new service:
mig cleanup-old --old old.sqlite
```

## Features

- F# schema reflection from `.fsx` scripts
- FK-aware bulk copy and replay with ID mapping
- Replay checkpoints and drain safety validation
- Schema identity metadata (`schema_hash`, optional `schema_commit`) persisted in new database
- Operational status reporting for old/new database migration state
- Optional old-database migration-table cleanup after cutover

## Specs

- [`specs/database_dsl.md`](./specs/database_dsl.md)
- [`specs/hot_migrations.md`](./specs/hot_migrations.md)
- [`specs/mig_command.md`](./specs/mig_command.md)
- [`specs/operator_runbook.md`](./specs/operator_runbook.md)

## Commands

- `mig migrate [--old <path>] [--schema <path>] [--schema-commit <value>] [--new <path>]` - Create the new DB from schema, copy data, and start recording on old DB (`mig migrate` defaults to current-directory auto-discovery and `./<dir>-<schema-hash>.sqlite` target naming; commit metadata defaults to `MIG_SCHEMA_COMMIT` when set).
- `mig drain --old <path> --new <path>` - Switch old DB to draining mode and replay pending migration log entries.
- `mig cutover --new <path>` - Verify drain completion, switch new DB to `ready`, and remove replay-only tables.
- `mig cleanup-old --old <path>` - Optional cleanup of old DB migration tables (`_migration_marker`, `_migration_log`).
- `mig status --old <path> [--new <path>]` - Show marker/status state and migration counters for operational visibility.

## Contributing

How to contribute:

- Open an issue to discuss the change and approach
- Add relevant tests
- Create a pull request mentioning the issue and also including a summary of the problem and approach to solve it
- Wait for the review

## Acknowledgments

This project wouldn't have been possible without the amazing open-source community. We're especially grateful to:

- **[Fabulous.AST][fabulous-ast]** - An elegant F# DSL for code generation that made creating and manipulating F# AST a joy
- **[Fantomas][fantomas]** - The excellent F# code formatter that ensures our generated code is beautiful and consistent

If you find these projects valuable, please consider supporting them:
- Star their repositories
- Contribute to their projects
- Donate to support their continued development

## License

[Apache 2.0][apache2]

[logo]: https://raw.githubusercontent.com/lamg/migrate/refs/heads/master/images/logo_small.png
[dotnet]: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
[apache2]: https://opensource.org/license/apache-2-0
[migtool]: https://www.nuget.org/packages/migtool
[nuget-version]: https://img.shields.io/nuget/v/migtool?style=flat-square
[nuget-downloads]: https://img.shields.io/nuget/dt/migtool?style=flat-square
[tests]: https://img.shields.io/github/actions/workflow/status/lamg/migrate/test.yml?style=flat-square&label=tests

[dotnet-badge]: https://img.shields.io/badge/.NET-10.0-blue?style=flat-square
[apache-badge]: https://img.shields.io/badge/License-Apache2-yellow.svg?style=flat-square
[fs-badge]: https://img.shields.io/badge/Language-F%23-blue?style=flat-square

[fabulous-ast]: https://github.com/edgarfgp/Fabulous.AST
[fantomas]: https://github.com/fsprojects/fantomas
