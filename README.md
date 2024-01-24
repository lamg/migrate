<p align="center">
  <img
    src="https://raw.githubusercontent.com/lamg/migrate/master/doc/images/logo.png"
    alt="Migrate – Declarative migrations with SQL"
    style="border-radius: 50%;width: 100px"
  />
</p>

[![NuGet Version][nuget-version]][migtool]
[![NuGet Downloads][nuget-downloads]][migtool]
![Tests][tests]

Migrate is a tool for migrating databases in a declarative way. It can be used from the command line or as a [library][MigrateLib].

## Installation

If you just want to test the tool without installing [.Net][dotnet],
then you can use a Docker image:

```sh
docker run -it 'mcr.microsoft.com/dotnet/nightly/sdk:8.0' bash
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
mkdir my_database_project
cd my_database_project
mig init
# generated project files with example definitions
mig status
# output shows migration for existing definitions
mig commit
# executes migration
mig log -s
# output shows migration metadata and a summary of executed steps
```

## Usage

See [usage](doc/usage.md)

## Why Migrate?

See [motivation](doc/motivation.md)

## Contributing

See [contributing_guideline](doc/contributing_guideline.md)

## License

[Apache 2.0][apache2]

[dotnet]: https://dotnet.microsoft.com/en-us/download/dotnet/8.0

[apache2]: https://www.apache.org/licenses/LICENSE-2.0

[migtool]: https://www.nuget.org/packages/migtool
[MigrateLib]: https://www.nuget.org/packages/MigrateLib
[nuget-version]: https://img.shields.io/nuget/v/migtool?style=flat-square
[nuget-downloads]: https://img.shields.io/nuget/dt/migtool?style=flat-square
[tests]: https://img.shields.io/github/actions/workflow/status/lamg/migrate/test.yml?style=flat-square&label=tests