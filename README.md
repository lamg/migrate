[![logo][logo]][migtool]

[![.NET][dotnet-badge]](https://dotnet.microsoft.com/)
[![F#][fs-badge]](https://fsharp.org/)
[![License: Apache2][apache-badge]][apache2]
[![NuGet Version][nuget-version]][migtool]
[![NuGet Downloads][nuget-downloads]][migtool]
![Tests][tests]

Migrate is a SQLite-first migration toolchain built around `Schema.fs`, generated `Db.fs`, and compiled schema modules.
It provides both a hot-migration workflow (`migrate` -> `drain` -> `cutover`) and a one-shot offline workflow (`offline`), plus type-safe code generation from compiled schema types.

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

For library usage:

- install `MigLib` when you only need the database/runtime surface
- install `MigLib.Web` in addition when you want ASP.NET Core `webResult` helpers

## Local Tool Build/Install (FAKE)

Build, pack, and install the current branch as a global `mig` tool from the local package output:

```sh
dotnet fsi build.fsx
```

Run specific FAKE targets when needed:

```sh
dotnet fsi build.fsx -- --target Build
dotnet fsi build.fsx -- --target PackTool
```

Run the installed local tool directly:

```sh
mig --help
```

## Quickstart (Online Migration)

Assuming:

- an existing SQLite database named `<dir>-<old-hash>.sqlite`
- a compiled generated `Db` module produced from `Schema.fs`

```sh
# from your project directory:
# - expects exactly one source db matching <dir>-<old-hash>.sqlite
# - derives target db from <Module>.DbFile
mig plan --assembly /path/to/App.dll --module Db
mig migrate --assembly /path/to/App.dll --module Db

# then continue in the same directory (paths auto-resolve)
mig status --assembly /path/to/App.dll --module Db
mig drain --assembly /path/to/App.dll --module Db
mig cutover --assembly /path/to/App.dll --module Db
# optional, after traffic has fully moved to the new service:
mig archive-old --assembly /path/to/App.dll --module Db

# from a different directory:
mig migrate -d /path/to/project --assembly /path/to/App.dll --module Db

# if migrate fails and you need to clear failed migration artifacts:
mig reset --dry-run --assembly /path/to/App.dll --module Db
mig reset --assembly /path/to/App.dll --module Db
```

## Quickstart (Schema Initialization)

When you want to bootstrap a database directly from a compiled generated `Db` module (no source DB yet):

```sh
mig init --assembly /path/to/App.dll --module Db
```

## Quickstart (Offline Migration)

When downtime is acceptable and you want to create the fully migrated target DB in one command from a compiled generated `Db` module:

```sh
# - expects exactly one source db matching <dir>-<old-hash>.sqlite
# - derives target db from <Module>.DbFile
# - archives the old db into ./archive/ after the copy succeeds
mig offline --assembly /path/to/App.dll --module Db
```

## Features

- Schema reflection from compiled F# modules
- FK-aware bulk copy and replay with ID mapping
- Replay checkpoints and drain safety validation
- Schema identity metadata (`schema_hash`, optional `schema_commit`) persisted in new database
- Operational status reporting for old/new database migration state
- Optional old-database archival into `archive/` after cutover
- Transactional ASP.NET Core request composition via the separate `MigLib.Web` package and `webResult`

## Specs

- [`specs/database_dsl.md`](./specs/database_dsl.md)
- [`specs/hot_migrations.md`](./specs/hot_migrations.md)
- [`specs/mig_command.md`](./specs/mig_command.md)
- [`specs/operator_runbook.md`](./specs/operator_runbook.md)
- [`specs/web_result.md`](./specs/web_result.md)

## Commands

- `mig init [--dir|-d <path>] [--assembly|-a <path>] [--module|-m <name>]` - Create a schema-matched database from a compiled generated module and apply seed inserts (no source DB required).
- `mig codegen [--dir|-d <path>] [--assembly|-a <path>] [--schema-module|-s <name>] [--module|-m <name>] [--output|-o <file>]` - Generate `Db.fs` from `Schema.fs` plus a compiled schema module, and emit a `DbFile` literal for the schema-bound SQLite filename.
- `mig offline [--dir|-d <path>] [--assembly|-a <path>] [--module|-m <name>]` - Create the fully migrated target DB in one step from a compiled generated module, then archive the old DB into `archive/`.
- `mig migrate [--dir|-d <path>] [--assembly|-a <path>] [--module|-m <name>]` - Create the new DB from a compiled generated module, copy data, and start recording on old DB.
- `mig plan [--dir|-d <path>] [--assembly|-a <path>] [--module|-m <name>]` - Print dry-run inferred paths, schema diff summary, and replay prerequisites without mutating DBs using a compiled generated module.
- `mig drain [--dir|-d <path>] [--assembly|-a <path>] [--module|-m <name>]` - Switch old DB to draining mode and replay pending migration log entries.
- `mig cutover [--dir|-d <path>] [--assembly|-a <path>] [--module|-m <name>]` - Verify drain completion plus old marker/log replay safety, switch new DB to `ready`, and remove replay-only tables.
- `mig archive-old [--dir|-d <path>] [--assembly|-a <path>] [--module|-m <name>]` - Optional archival of the old DB into `archive/`, replacing any existing archive with the same name.
- `mig reset [--dir|-d <path>] [--assembly|-a <path>] [--module|-m <name>] [--dry-run]` - Reset failed/aborted migration artifacts, or inspect reset impact without mutating DB files.
- `mig status [--dir|-d <path>] [--assembly|-a <path>] [--module|-m <name>]` - Show marker/status state and migration counters for operational visibility.

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
