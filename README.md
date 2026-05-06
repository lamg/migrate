[![logo][logo]][migtool]

[![.NET][dotnet-badge]](https://dotnet.microsoft.com/)
[![F#][fs-badge]](https://fsharp.org/)
[![License: Apache2][apache-badge]][apache2]
[![NuGet Version][nuget-version]][migtool]
[![NuGet Downloads][nuget-downloads]][migtool]
![Tests][tests]

Migrate is a SQLite-first migration toolchain built around a schema project, generated `Db.fs`, and compiled schema modules.
It generates typed CRUD/query helpers from compiled schema definitions and provides blocking `init`, `plan`, `migrate`, `status`, and `reset` workflows for schema-bound SQLite files.

## Project Convention

The current convention is:

1. Keep the runtime project in the working directory.
2. Keep the schema project at `MigSchema/MigSchema.fsproj`.
3. Keep the schema source at `MigSchema/MigSchema.fs`.
4. Run `mig codegen` from the runtime project directory.
5. Let `mig codegen` write `Db.fs` into the runtime project root.
6. Build the runtime project after code generation so the compiled runtime assembly contains the generated module.

The runtime project must define `<RootNamespace>`. `mig codegen` uses it to generate the runtime module `<RootNamespace>.Db` and to derive the SQLite filename prefix.

The schema project must also define `<RootNamespace>`. The compiled schema module is resolved as `<SchemaRootNamespace>.MigSchema`.

## Generated Code

`mig codegen` emits a runtime module that contains:

- `GeneratedSchema`
- generated record and DU types
- generated CRUD/query helpers driven by schema annotations

Examples of generated helpers include:

- `Student.Insert`
- `Student.InsertOrIgnore`
- `Student.SelectById`
- `Student.SelectAll`
- `Student.SelectByName`
- `Student.SelectNameLike`
- `Student.SelectByNameOrInsert`
- `Student.Upsert`

## Installation

Install the CLI as a global tool:

```sh
dotnet tool install --global migtool
```

For library usage:

- install `MigLib` for schema attributes, generated CRUD support, transactions, and migration workflows
- install `MigLib.Web` when you also want the ASP.NET Core `webResult` helpers

## Local Tool Build

Build and install the current branch as a global `mig` tool from the local package output:

```sh
dotnet fsi build.fsx install
```

## Quickstart: Init

Build the schema project, generate `Db.fs`, build the runtime project, and initialize the schema-bound database:

```sh
dotnet build ./my-app/MigSchema/MigSchema.fsproj
mig codegen -d ./my-app
dotnet build ./my-app/my-app.fsproj
mig init -d ./my-app
```

`mig init` is idempotent at the workflow level: when the target database already exists, it reports success and does not recreate it.

## Quickstart: Migrate

When a previous schema-bound SQLite file already exists for the same app/instance prefix, `mig migrate` creates the new target database, copies compatible data, and archives the old file into `archive/` next to the database directory.

```sh
dotnet build ./my-app/MigSchema/MigSchema.fsproj
mig codegen -d ./my-app
dotnet build ./my-app/my-app.fsproj
mig plan -d ./my-app
mig migrate -d ./my-app
mig status -d ./my-app
```

If you need to discard the current target database and restore the latest archived database back into the main database directory:

```sh
mig reset -d ./my-app
```

## Runtime Use

Generated helpers execute inside `dbTxn` or `txn` transaction workflows.

```fsharp
open System.IO
open MigLib
open MigLib.Db.Transactions
open MyApp.Db

let project =
  MigLib.resolveProjectFromGeneratedSchema dataDirectory None GeneratedSchema
  |> fun task -> task.Result
  |> Result.defaultWith (fun error -> failwithf "%A" error)

let db = dbTxn project.targetDbPath

let result =
  db {
    let! student = Student.SelectByNameOrInsert { Id = 0L; Name = "Alice"; Age = 21L }
    let! matches = Student.SelectNameLike "li"
    return student, matches
  }
```

## Example

`example/` shows the full convention in a working project:

- runtime project at `example/example.fsproj`
- schema project at `example/MigSchema/MigSchema.fsproj`
- generated `Db.fs` at `example/Db.fs`
- generated CRUD usage in `example/Program.fs`
- scripted `init` and `migrate` flows in `example/build.fsx`

Run it with:

```sh
dotnet fsi example/build.fsx
```

## Commands

- `mig codegen`: generate `Db.fs` from the compiled `MigSchema` module and schema source file
- `mig init`: create the current schema-bound database when it does not exist yet
- `mig plan`: report inferred source/target paths plus supported and unsupported schema differences
- `mig migrate`: create the target database, copy data, and archive the previous source database
- `mig status`: show the current database path, archived databases, and whether a migration source is still present
- `mig reset`: delete the current target database and restore the latest archived database into the main database directory

## Specs

- [`specs/database_dsl.md`](./specs/database_dsl.md)
- [`specs/mig_command.md`](./specs/mig_command.md)
- [`specs/operator_runbook.md`](./specs/operator_runbook.md)
- [`specs/custom_migration.md`](./specs/custom_migration.md)
- [`specs/web_result.md`](./specs/web_result.md)

## Contributing

- Open an issue to discuss the change and approach.
- Add relevant tests.
- Open a pull request with the problem statement and solution summary.

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
