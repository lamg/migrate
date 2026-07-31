# Example

SQL-first migrate sample. Codegen emits one module file per annotated relation under `Stores/`, plus `Stores/Migrations.fs` whose `scripts` values are ordinary F# string constants (compiled into the executable; AOT-friendly).

```sh
# regenerate Stores/*.fs (relations + Migrations.fs)
dotnet run --project ../src/mig/mig.fsproj -- codegen \
  -m Migrations -o Stores -n Example.Db

dotnet run --project example.fsproj
```

Author DDL in `Migrations/*.sql` (dev only). At runtime the example calls `migrateScripts dbPath Migrations.scripts` — no SQL directory, no embedded resources, no reflection.
