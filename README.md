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

## Local Tool Build/Install (FAKE)

Build and install the current branch as a local tool into `./.tools/mig`:

```sh
dotnet fsi build.fsx
```

Run specific FAKE targets when needed:

```sh
dotnet fsi build.fsx -- --target Build
dotnet fsi build.fsx -- --target PackTool
```

Use a custom local package version when needed:

```sh
MIG_LOCAL_VERSION=0.0.0-local.1 dotnet fsi build.fsx
```

Run the installed local tool directly:

```sh
./.tools/mig/mig --help
```

## Quickstart (Online Migration)

Assuming:

- an existing SQLite database named `<dir>-<old-hash>.sqlite`
- a target schema script at `schema.fsx`

```sh
# from your project directory:
# - expects ./schema.fsx
# - expects exactly one source db matching <dir>-<old-hash>.sqlite
# - derives target db as <dir>-<schema-hash>.sqlite
mig plan
mig migrate

# then continue in the same directory (paths auto-resolve)
mig status
mig drain
mig cutover
# optional, after traffic has fully moved to the new service:
mig cleanup-old

# from a different directory:
mig migrate -d /path/to/project

# if migrate fails and you need to clear failed migration artifacts:
mig reset --dry-run
mig reset
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

- `mig migrate [--dir|-d <path>]` - Create the new DB from schema, copy data, and start recording on old DB.
- `mig plan [--dir|-d <path>]` - Print dry-run inferred paths, schema diff summary, and replay prerequisites without mutating DBs.
- `mig drain [--dir|-d <path>]` - Switch old DB to draining mode and replay pending migration log entries.
- `mig cutover [--dir|-d <path>]` - Verify drain completion plus old marker/log replay safety, switch new DB to `ready`, and remove replay-only tables.
- `mig cleanup-old [--dir|-d <path>]` - Optional cleanup of old DB migration tables (`_migration_marker`, `_migration_log`).
- `mig reset [--dir|-d <path>] [--dry-run]` - Reset failed/aborted migration artifacts, or inspect reset impact without mutating DB files.
- `mig status [--dir|-d <path>]` - Show marker/status state and migration counters for operational visibility.

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
