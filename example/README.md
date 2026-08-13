# Example

SQL-first migrate sample. Codegen emits one module file per annotated relation under `Stores/`, plus `Stores/Migration.fs` with `Migration.migrate`.

```sh
# regenerate Stores/*.fs (relations + Migration.fs)
dotnet run --project ../src/mig/mig.fsproj -- codegen \
  -m schema -o Stores -n Example.Db

dotnet run --project example.fsproj
```

Author DDL in `schema/` (dev only). At runtime the example calls `Migration.migrate dbPath`.
