## Hot migrations on deploy

This document defines the compiled-only model for using `MigLib` from a service.

The service owns two source files:

- `Schema.fs`: the schema source of truth
- `Db.fs`: fully generated code derived from `Schema.fs`

`MigLib` owns build-time generation, runtime migration execution, and the startup coordination primitives a service uses to decide whether it should start normally, wait, or perform the migration itself.

## Goals

- Keep build-time generation separate from runtime execution.
- Keep deployed services AOT-friendly.
- Keep `Db.fs` fully generated.
- Make services use `MigLib` as a library instead of reimplementing migration logic.
- Fail clearly when a schema transition is outside the supported declarative subset.

## Build-time model

The normal build flow is:

1. The service compiles `Schema.fs`.
2. Build code calls `MigLib.Build`.
3. `MigLib.Build` generates or refreshes `Db.fs`.
4. The service compiles the generated `Db.fs`.

The generated `Db.fs` contains:

- generated query helpers
- `DbFile`
- `Schema`
- `SchemaHash`
- `SchemaIdentity`

At build time, services should call the same `MigLib.Build` API regardless of whether generation is triggered from `build.fsx`, an MSBuild hook, or another build step.

Example build step:

```fsharp
open System.IO
open MigLib.Build
open MigLib.Util

let generateDb (projectDir: string) (compiledAssemblyPath: string) =
  result {
    let schemaPath = Path.Combine(projectDir, "Schema.fs")
    let dbPath = Path.Combine(projectDir, "Db.fs")

    return!
      generateDbCodeFromAssemblyModulePath
        "Db"
        schemaPath
        compiledAssemblyPath
        "Schema"
        dbPath
  }
```

## Runtime model

At runtime, the service uses only compiled code and SQLite files.

The generated `DbFile` value names the schema-bound target SQLite file. The runtime-configured SQLite directory controls where that file is created or looked up.

When a service instance starts, it should ask `MigLib.Db` what to do with the target database:

- `UseExisting dbPath`: the target DB is ready, start normally
- `WaitForMigration dbPath`: another instance is migrating it, wait
- `MigrateThisInstance dbPath`: this instance should perform the migration
- `ExitEarly (dbPath, reason)`: the target DB is in an invalid state, fail startup

The service should treat migration failure as a startup failure.

## Service startup

Services should keep startup orchestration separate from normal request handling, but they should usually do that by calling `startService` instead of branching on lower-level migration states themselves.

There is one relevant runtime path input for this flow: the directory containing the SQLite file whose name is in `Db.DbFile`. The service chooses the environment variable name for that directory and passes it to `startService`. `MigLib` should not hardcode that env var name.

`startService` encapsulates the service-startup cases:

- use the ready target DB
- wait for another instance to finish migrating the target DB
- initialize a brand new target DB when there is no previous DB
- migrate from the previous DB when one exists
- fail startup when the target DB is invalid or migration fails

Example:

```fsharp
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Db
open Mig.HotMigration

type Env = { dbTxn: DbTxnBuilder }

let decideStartup () =
  startService
    "MYAPP_SQLITE_DIR"
    Db.DbFile
    Db.SchemaIdentity
    Db.Schema
    CancellationToken.None

let startApp (ct: CancellationToken) : Task<Result<Env, SqliteException>> =
  task {
    let! dbTxn =
      startService
        "MYAPP_SQLITE_DIR"
        Db.DbFile
        Db.SchemaIdentity
        Db.Schema
        ct

    return dbTxn |> Result.map (fun dbTxn -> { dbTxn = dbTxn })
  }
```

The returned `DbTxnBuilder` is the only database handle most services need during normal operation.

Then request code uses the generated query helpers through that environment:

```fsharp
let createStudent (env: Env) (student: Student) =
  env.dbTxn {
    let! actualId = Student.Insert student
    return actualId
  }
```

If the service needs a non-default wait interval, it should use `startServiceWithPolling` instead. If it needs custom orchestration beyond that, it can still call the lower-level migration primitives directly.

The service can still wrap this in its own logging, leader-election, health-check, or process-exit policy, but the database decisions themselves should come from `MigLib`.

## Automatic versus explicit migration behavior

Automatic mapping is allowed only when it does not imply data corruption or silent loss.

Examples that should map automatically:

- source and target tables or columns with identical names and compatible types
- nullable column additions
- added columns with defaults that can be applied safely

Ambiguous transitions must be explicit in `Schema.fs`.

Current explicit metadata includes:

- `PreviousName`
- `DropColumn`

If a migration cannot be derived from the supported `Schema.fs` primitives, planning or migration should fail clearly.

## Responsibilities split

`MigLib` owns:

- generation of `Db.fs`
- generated query helpers
- startup target-database state inspection
- default previous-database inference for startup
- waiting for target-database readiness
- migration execution against a known old DB and known target DB
- the hot-migration primitives used by services and by `mig`

The service owns:

- choosing when generation runs in its build
- choosing the runtime SQLite directory env var name
- logging and process lifecycle policy
- deciding whether to use direct startup migration, a leader-only migration process, or another deployment policy around the same `MigLib` calls

## `mig` CLI role

The `mig` CLI is a thin operational wrapper over the same `MigLib` capabilities.

It should not contain a separate migration implementation. Services and the CLI should rely on the same underlying build-time and runtime library APIs.
