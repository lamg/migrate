[![logo][logo]][migtool]

[![.NET][dotnet-badge]](https://dotnet.microsoft.com/)
[![F#][fs-badge]](https://fsharp.org/)
[![License: Apache2][apache-badge]][apache2]
[![NuGet Version][nuget-version]][migtool]
[![NuGet Downloads][nuget-downloads]][migtool]
![Tests][tests]

Migrate is a tool for performing declarative migrations by finding differences between an expected database schema and the existing one, currently in a SQLite database. It also generates type-safe F# code with CRUD operations for your database schema.

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

## Quickstart

```sh
mkdir test_db
cd test_db
mig init
# generated project files with example definitions
mig status
# output shows migration for existing definitions
mig commit
# generates and executes migration
mig log
# output shows migration metadata and a summary of executed steps
mig codegen
# generates type-safe F# code with CRUD operations
```

## Features

- [Declarative migrations](./src/MigLib/DeclarativeMigrations/README.md)
- [Migration execution](./src/MigLib/Execution/README.md)
- [Migration execution as library](./src/MigLib/Execution/README.md#migration-execution-using-miglib)
- [F# code generation with type-safe CRUD operations](./src/MigLib/CodeGen/README.md)
- [Migration log](./src/MigLib/MigrationLog/README.md)
- [Seed data management with idempotent upserts](./spec.md#9-seed-data-management-with-idempotent-upserts)

## Commands

- `mig init` - Initialize a new migration project with example schema files
- `mig status` - Generate migration SQL by comparing expected schema with current database
- `mig commit [-m <message>]` - Generate and execute migrations step by step
- `mig schema` - Show the current database schema
- `mig log [-s <steps-id>]` - Show migration history and execution metadata
- `mig codegen [-d <directory>]` - Generate type-safe F# code with CRUD operations from SQL schema files
- `mig seed` - Execute seed statements (INSERT OR REPLACE) from SQL files

## Contributing

How to contribute:

- Open an issue to discuss the change and approach
- Add relevant tests
- Create a pull request mentioning the issue and also including a summary of the problem and approach to solve it
- Wait for the review

## Acknowledgments

This project wouldn't have been possible without the amazing open-source community. We're especially grateful to:

- **[Fabulous.AST](https://github.com/edgarfgp/Fabulous.AST)** - An elegant F# DSL for code generation that made creating and manipulating F# AST a joy
- **[Fantomas](https://github.com/fsprojects/fantomas)** - The excellent F# code formatter that ensures our generated code is beautiful and consistent

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
