# Migrate

[![NuGet][nuget-shield]][nuget]
![Tests][tests]

Migrate is a tool for migrating databases in a declarative way. It can be used from the command line or as a library.

## Installation

If you just want to test the tool without installing [.Net][0],
then you can use a Docker image:

```sh
docker run -it 'mcr.microsoft.com/dotnet/nightly/sdk:8.0' bash
```

Inside the container run:

```sh
export PATH="$PATH:/root/.dotnet/tools"
```

After having [.Net][0] in your system you can run

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

[Apache 2.0][1]

[0]: https://dotnet.microsoft.com/en-us/download/dotnet/8.0

[1]: https://www.apache.org/licenses/LICENSE-2.0

[nuget]: https://www.nuget.org/packages/migtool

[nuget-shield]: https://buildstats.info/Nuget/migtool

[tests]: https://github.com/lamg/migrate/workflows/tests/badge.svg