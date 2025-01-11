[![logo][logo]][migtool]

[![NuGet Version][nuget-version]][migtool]
[![NuGet Downloads][nuget-downloads]][migtool]
![Tests][tests]

Migrate is a tool for performing declarative migrations by finding differences between an expected database schema, and the existing one, currently in a SQLite database.

## Installation

If you just want to test the tool without installing [.Net][dotnet],
then you can use a Docker image:

```sh
docker run -it 'mcr.microsoft.com/dotnet/nightly/sdk:9.0' bash
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
mig gen
# output shows migration for existing definitions
mig exec
# executes migration
mig log
# output shows migration metadata and a summary of executed steps
```

## Features

- [Declarative migrations](./src/MigLib/DeclarativeMigrations/README.md)
- [Migration execution](./src/MigLib/Execution/README.md)
- [Migration execution as library](./src/MigLib/Execution/README.md#migration-execution-using-miglib)
- [Import Goose migrations](./src/MigLib/ImportGoose/README.md)
- [Migration log](./src/MigLib/MigrationLog/README.md)

## Contributing

Areas where contributions are welcomed:

- Support for other RDBMS like PostgresSQL
- SQL generation
- SQL parsing
- bug fixes
- installation, packaging and release process

How to contribute:

- Open an issue to discuss the change and approach
- Add relevant tests
- Create a pull request mentioning the issue and also including a summary of the problem and approach to solve it
- Wait for the review

See [contributing_guideline](doc/contributing_guideline.md)

## License

[Apache 2.0][apache2]

[logo]: https://raw.githubusercontent.com/lamg/migrate/refs/heads/master/images/logo_small.png
[dotnet]: https://dotnet.microsoft.com/en-us/download/dotnet/9.0
[apache2]: https://www.apache.org/licenses/LICENSE-2.0
[migtool]: https://www.nuget.org/packages/migtool
[nuget-version]: https://img.shields.io/nuget/v/migtool?style=flat-square
[nuget-downloads]: https://img.shields.io/nuget/dt/migtool?style=flat-square
[tests]: https://img.shields.io/github/actions/workflow/status/lamg/migrate/test.yml?style=flat-square&label=tests
