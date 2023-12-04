# Migrate

Migrate is a tool for migrating databases in a declarative way. It can be used from the command line or as a library.

## Installation

Run `dotnet tool install --global migtool`

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
