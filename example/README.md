# Example

This example shows the compiled-schema flow end to end:

1. Build `Schema.fsproj`
2. Generate `Db.fs` from the compiled `Schema` module
3. Run `Program.fs`
4. Let `MigLib` migrate a legacy SQLite database into the schema-bound `Db.DbFile`
5. Query the migrated database through generated helpers such as `Student.SelectAll`, `Student.Insert`, and `Student.SelectByName`

Run it with:

```sh
dotnet fsi build.fsx
```

Or target individual steps:

```sh
dotnet fsi build.fsx -- --target BuildSchema
dotnet fsi build.fsx -- --target Codegen
dotnet fsi build.fsx -- --target BuildExample
dotnet fsi build.fsx -- --target Run
```
