# Progress

## Fresh start (2026-02-17)

Deleted old project (src/, spec.md, PROGRESS.md) and created new skeleton based on specs in `specs/`:

- `specs/database_dsl.md` — F# DSL where types + attributes define database schema
- `specs/hot_migrations.md` — three-phase hot migration strategy (Migrate → Drain → Cutover)
- `specs/mig_command.md` — CLI commands: `migrate`, `drain`, `cutover`, `status`

## Current structure

```
src/
├── Directory.Packages.props   (central package management)
├── migrate.slnx               (solution with 3 projects)
├── MigLib/
│   ├── MigLib.fsproj
│   └── Db.fs                  (TaskTxnBuilder CE + attribute types)
├── mig/
│   ├── mig.fsproj
│   └── Program.fs             (Argu CLI: migrate, drain, cutover, status)
└── Test/
    ├── Test.fsproj
    └── Tests.fs               (placeholder xunit test)
```

## What's implemented

- **MigLib/Db.fs**: All DSL attribute types (AutoIncPK, PK, Unique, Default, DefaultExpr, Index, SelectAll, SelectBy, SelectOneBy, SelectLike, SelectByOrInsert, UpdateBy, DeleteBy, InsertOrIgnore, OnDeleteCascade, OnDeleteSetNull, View, Join, LeftJoin, ViewSql, OrderBy) and TaskTxnBuilder CE skeleton (Run, Zero, Return, Bind, Combine, Delay, For)
- **mig/Program.fs**: Argu CLI with MigrateArgs, DrainArgs, CutoverArgs, StatusArgs and stub dispatch functions
- **Test/Tests.fs**: Single placeholder test

## What's next

1. SQL generation from F# types via reflection (schema .fsx evaluation)
2. Schema diffing and column mapping
3. Bulk data copy with FK dependency ordering and ID mapping
4. Migration log recording in TaskTxnBuilder
5. Drain replay logic
6. Cutover and status commands
