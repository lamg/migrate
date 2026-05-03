# Example

This example follows the current project convention:

1. `example.fsproj` is the runtime project.
2. `MigSchema/MigSchema.fsproj` compiles the schema source.
3. `mig codegen` writes `Db.fs` into the runtime project root.
4. `mig init` or `mig migrate` creates the schema-bound SQLite file.
5. `Program.fs` opens that database with `dbTxn` and uses generated CRUD helpers such as `Student.Insert`, `Student.SelectByName`, `Student.SelectNameLike`, `Student.SelectByNameOrInsert`, and `Student.Upsert`.

Run the migration example end to end:

```sh
dotnet fsi build.fsx
```

That target:

1. builds `MigSchema`
2. runs `mig codegen`
3. builds the runtime project
4. creates a legacy SQLite file
5. runs `mig migrate`
6. runs `Program.fs` against the migrated target database

Run the init-only flow instead:

```sh
dotnet fsi build.fsx -- --target RunInitExample
```

Useful individual targets:

```sh
dotnet fsi build.fsx -- --target Clean
dotnet fsi build.fsx -- --target Restore
dotnet fsi build.fsx -- --target BuildSchema
dotnet fsi build.fsx -- --target Codegen
dotnet fsi build.fsx -- --target BuildExample
dotnet fsi build.fsx -- --target CreateLegacySource
dotnet fsi build.fsx -- --target Migrate
dotnet fsi build.fsx -- --target RunProgram
```
