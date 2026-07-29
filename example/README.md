# Example

SQL-first migrate sample. Codegen emits one module file per annotated relation under `Stores/`.

```sh
# regenerate Stores/*.fs (one file per relation)
dotnet run --project ../src/mig/mig.fsproj -- codegen \
  -m Migrations -o Stores -n Example.Db

dotnet run --project example.fsproj
```
