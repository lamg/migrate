# Example

This example follows the current project convention:

1. `example.fsproj` is the runtime project.
2. `MigSchema/MigSchema.fsproj` compiles the schema source.
3. `mig codegen` writes `Db.fs` into the runtime project root.
4. `mig init` or `mig migrate` creates the schema-bound SQLite file.
5. `Program.fs` resolves the schema-bound database path from `GeneratedSchema`, opens it with `dbTxn`, and uses generated CRUD helpers such as `Student.Insert`, `Student.SelectByName`, `Student.SelectNameLike`, `Student.SelectByNameOrInsert`, and `Student.Upsert`.

Run the example end to end:

```sh
dotnet fsi build.fsx
```

That target:

1. builds `MigSchema`
2. runs `mig codegen`
3. builds the runtime project
4. runs `mig init`
5. runs `Program.fs` against the initialized target database

Useful individual targets:

```sh
dotnet fsi build.fsx clean
dotnet fsi build.fsx restore
dotnet fsi build.fsx build-schema
dotnet fsi build.fsx codegen
dotnet fsi build.fsx build-example
dotnet fsi build.fsx init
dotnet fsi build.fsx run
```
