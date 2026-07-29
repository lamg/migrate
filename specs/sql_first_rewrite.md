# SQL-first Migrate Rewrite

Status: implemented on branch `rewrite/sql-first-dbup` (v10 greenfield)

## Goal

Rewrite migrate so that:

1. **Filesystem SQL migrations** are the schema source of truth.
2. **`mig codegen`** generates F# types and query helpers only for relations the user annotates.
3. **Runtime** uses `Microsoft.Data.Sqlite` and MigLib’s existing transaction model (`DbTxnBuilder` / `TxnStep`).
4. **No** F#-first schema DSL, normalization, SqlProvider, DbUp, or declarative hot-migrate pipeline.

First dogfood consumer: **marketbot** (replace SqlProvider stores).

## Non-goals (for now)

- `mig migrate` / `mig status` CLI commands (may return later).
- Custom named-SQL ops (use SQL views + `select_by` instead).
- True LINQ / `query { }` provider.
- Fantomas on generated output (user formats).
- Generating a `Data/` directory (connection, CE, migration runner wiring stay in MigLib + user app).
- Parsing DDL structure when introspection can supply it.

## Architecture

```
User app
  Migrations/*.sql          -- ordered *.sql + -- mig: annotations
  Stores/*.fs               -- one generated module file per relation
  (hand-written companions) -- co-located helpers next to generated modules
  startup / build.fsx       -- MigLib.codegen + MigLib.migrate helpers

mig codegen  (CLI and MigLib public API)
  1. Collect migration scripts (ordered by file name)
  2. Apply via MigLib filesystem migrator to temporary SQLite
  3. Introspect tables/views/columns/PKs (PRAGMA / catalog)
  4. Scan scripts for `-- mig:` annotation lines only
  5. Emit one .fs file per annotated relation into --output directory

MigLib
  - DbTxnBuilder / TxnStep / readOnlyDbTxn (current model)
  - Shared helpers used by generated code (read/map/exec/bind params)
  - Public codegen function (same behavior as CLI; for build.fsx)
  - Public filesystem migrate: migrateScripts : dbPath -> dir -> Task<Result<unit,string>>
```

Package layout: **`migtool` CLI + `MigLib` library** only.

## Source of truth split

| Concern | Owner |
|--------|--------|
| DDL, seeds, backfills | `*.sql` scripts in the app |
| Applied-script journal | MigLib (`SchemaVersions` table) |
| Typed F# surface | `mig codegen` / `MigLib` codegen API from annotations + introspection |
| Running migrations | User app, via MigLib convenience function |
| Normalization | User (out of scope) |

## Annotation language (style 1)

One-line SQL comments immediately before a `CREATE TABLE` / `CREATE VIEW` (or the relation they annotate). Only relations with annotations participate in codegen.

### Relation headers

```sql
-- mig:rel User
-- mig:ops insert, select_by(email), select_by_id, upsert
-- mig:bool active
-- mig:datetime created_at
CREATE TABLE app_user (
  id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
  email TEXT NOT NULL,
  active INTEGER NOT NULL,
  created_at TEXT NOT NULL
) STRICT;
```

```sql
-- mig:ops select_by(email), select_all
CREATE VIEW active_user AS
  SELECT id, email, created_at FROM app_user WHERE active = 1;
-- F# name derived: ActiveUser
```

Rules:

- Unannotated tables/views → exist in DB only; **no** F# types or queries.
- A relation with **no ops** → **no** type and no queries (ops are the only reason to generate a type).
- `-- mig:rel Name` optionally overrides the F# type/module name. If omitted, derive from the SQL relation identifier (`app_user` → `AppUser`, `active_user` → `ActiveUser`).
- Use `-- mig:rel` for both tables and views (no separate `mig:table` / `mig:view`).
- Tables and views are both **relations**: same generation rules for a given column signature and **allowed** ops.
- **Write ops on views are refused at codegen** (`insert`, `insert_or_ignore`, `upsert`, `delete_*`). Views may only use read ops (`select_*`).

### Column type overrides

Default mapping from SQLite declared/affinity types (via introspection) to F#:

| SQLite | F# (NOT NULL) | F# (NULL) |
|--------|---------------|-----------|
| INTEGER | `int64` | `int64 option` |
| REAL | `float` | `float option` |
| TEXT | `string` | `string option` |
| BLOB | `byte[]` | `byte[] option` |

Overrides (one annotation per column, before the relation):

```sql
-- mig:bool active
-- mig:int status
-- mig:uint flags
-- mig:datetime created_at
```

| Annotation | F# type | Storage assumption |
|------------|---------|-------------------|
| `-- mig:bool col` | `bool` / `bool option` | INTEGER 0/1 |
| `-- mig:int col` | `int` / `int option` | INTEGER |
| `-- mig:uint col` | `uint32` / `uint32 option` | INTEGER (non-negative) |
| `-- mig:int64 col` | explicit `int64` (default) | INTEGER |
| `-- mig:datetime col` | `DateTimeOffset` / `DateTimeOffset option` | TEXT, RFC 3339 |

v1 override set: `bool`, `int`, `uint`, `int64`, `datetime`.

Nullability comes from the schema (NOT NULL vs NULL) for **tables**. SQLite’s `PRAGMA table_info` always reports view columns as nullable, so **view columns are treated as non-null** in codegen (matching apps that forbid SQL NULLs in projections). Overrides do not change nullability.

### Ops catalog (v1)

Comma-separated on `-- mig:ops ...`. Only catalogued ops; no free-form SQL.

| Op | Meaning | Generated shape (illustrative) | Tables | Views |
|----|---------|--------------------------------|--------|-------|
| `insert` | INSERT; omit autoincrement columns from input; return rowid when applicable | `Relation.insert : InsertInput -> TxnStep<...>` | yes | **no** |
| `insert_many` | batch `insert` over a sequence | `Relation.insertMany : InsertInput seq -> TxnStep<unit>` | yes | **no** |
| `insert_or_ignore` | INSERT OR IGNORE | `Relation.insertOrIgnore : ...` | yes | **no** |
| `upsert` | INSERT ON CONFLICT DO UPDATE; conflict target = **primary key only** | `Relation.upsert : ...` | yes | **no** |
| `upsert_many` | batch `upsert` over a sequence | `Relation.upsertMany : Row seq -> TxnStep<unit>` | yes | **no** |
| `select_all` | SELECT all columns of relation | `Relation.selectAll : TxnStep<Row list>` | yes | yes |
| `select_by_id` | SELECT by single-column PK | `Relation.selectById : pk -> TxnStep<Row option>` | yes | yes* |
| `select_by(col,...)` | SELECT WHERE equality on listed columns | `Relation.selectByEmail : ... -> TxnStep<Row list>` | yes | yes |
| `select_one_by(col,...)` | same, single row option | `Relation.selectOneByEmail : ... -> TxnStep<Row option>` | yes | yes |
| `select_like(col)` | WHERE col LIKE @pattern | `Relation.selectNameLike : string -> TxnStep<Row list>` | yes | yes |
| `select_top(col, n)` | ORDER BY col DESC LIMIT n (`n` compile-time positive int) | `Relation.selectTopCreatedAt200 : TxnStep<Row list>` | yes | yes |
| `select_bottom(col, n)` | ORDER BY col ASC LIMIT n | `Relation.selectBottomCreatedAt200 : TxnStep<Row list>` | yes | yes |
| `delete_by_id` | DELETE by single-column PK | `Relation.deleteById : pk -> TxnStep<int>` | yes | **no** |
| `delete_by(col,...)` | DELETE WHERE equality | `Relation.deleteByEmail : ... -> TxnStep<int>` | yes | **no** |
| `delete_all` | DELETE all rows | `Relation.deleteAll : TxnStep<int>` | yes | **no** |

\* `select_by_id` on a view requires a single-column primary key to be discoverable; if the view has no PK in SQLite’s catalog, refuse `select_by_id` and require `select_by` / `select_one_by` instead.

Notes:

- Composite primary keys: `select_by_id` / `delete_by_id` are invalid; use `select_by(a,b)` / `delete_by(a,b)`.
- `upsert` conflict target is the **primary key only** in v1 (no `upsert_on` yet).
- `select_by` returns **list**; use `select_one_by` when a unique row is expected.
- No custom SQL fragments in v1. Express filtered projections as **views**, then annotate with read ops.
- `select_top` / `select_bottom` bake the limit into the generated member (`select_top(created_at, 200)` → `selectTopCreatedAt200`); the limit is not a runtime argument.

**Insert inputs:** omit **only autoincrement columns**. Do **not** omit columns that merely have SQL `DEFAULT` values; callers must supply them.

Naming of generated members: PascalCase F# from snake_case columns and op names (`select_by(email)` → `selectByEmail`).

## Codegen pipeline

1. **Inputs**: migrations directory, **output directory**, root namespace (`--namespace` / API param).
2. **Apply scripts**: MigLib filesystem migrator against a temporary SQLite file.
3. **Introspect**: list tables and views; for each, columns, types, nullability, PK columns, autoincrement.
4. **Annotation scan** (minimal text scan, not a SQL parser):
   - Read each migration file in order.
   - Collect consecutive `-- mig:...` lines.
   - Associate them with the next `CREATE TABLE` / `CREATE VIEW` relation name (light tokenization of the statement head only).
   - Validate: annotated relation exists; override columns exist; ops allowed for table vs view; PK present where required.
5. **Emit F#**:
   - **One file per annotated relation**: `{Name}.fs` in the output directory (`module {namespace}.{Name}`).
   - No monolithic `Generated.fs` and no thin facade that only re-exports generated types.
   - Row type + op functions only for that relation’s ops.
   - Generated code calls **MigLib** shared helpers.
   - Stale auto-generated `.fs` files (header `// <auto-generated />`) for removed relations are deleted; hand-written files are left alone.
6. **No Fantomas** in the tool.

Annotation association scans SQL **text for comments and relation names**. Full DDL parsing is avoided by relying on the live schema after migrations.

## Generated code contract

- Depends on **MigLib**.
- Functions return / compose with **`TxnStep<'a>`** for `dbTxn` / `txn` / `readOnlyDbTxn`.
- Row types are plain F# records with PascalCase fields.
- Insert input types omit **autoincrement** columns only; all other columns (including those with defaults) are required fields.

Example:

```fsharp
dbTxn dbPath {
  match! User.selectOneByEmail email with
  | Some u -> return u
  | None ->
    let! id =
      User.insert
        { Email = email
          Active = true
          CreatedAt = DateTimeOffset.UtcNow }
    return!
      User.selectById id
      |> TxnStep.map Option.get // or equivalent helper
}
```

## MigLib surface (rewrite)

### Keep / rework

- Transaction CE: `DbTxnBuilder`, `TxnBuilder`, `dbTxn`, `readOnlyDbTxn`, `txn`, journal/busy options as today.
- `TxnStep<'a>` on `SqliteTransaction`.
- Shared execution helpers for codegen (parameter bind, reader map, exec, scalar, last_insert_rowid).

### Add

**Codegen (public, for CLI and `build.fsx`):**

```fsharp
// Shape illustrative; exact type names during implementation
val generate:
  migrationsDir: string ->
  outputPath: string ->
  namespace: string ->
  Result<CodegenResult, string>
// or Task<Result<...>> if async is natural
```

Same behavior as `mig codegen --namespace ...`.

**Filesystem migrate (public, for app startup and `build.fsx`):**

```fsharp
val migrateScripts:
  dbPath: string ->
  scriptsDirectory: string ->
  Task<Result<unit, string>>
```

Scripts are ordered by file name; applied names are stored in `SchemaVersions`. No assembly/embedded-script API (AOT-friendly). Errors as `Result`.

### Remove

- Schema reflection from F# assemblies.
- Declarative migrate/plan/copy/archive pipeline.
- Attribute DSL as schema source.
- Normalization / normalized query generators.
- Any SqlProvider coupling.

## CLI surface (v1)

| Command | Role |
|---------|------|
| `mig codegen` | Apply migrations to temp DB, introspect, read annotations, emit one `.fs` module per relation |

Flags (minimum):

- migrations directory
- `--output` directory (one `{Name}.fs` per relation)
- `--namespace` root F# namespace

Out of scope for now: `mig migrate`, `mig status`, `mig init`, `mig plan`, `mig reset`. Migration is done from the app or `build.fsx` via MigLib public functions.

## Repository rewrite strategy

- **Greenfield under the same repo**: replace `src/` (and obsolete specs that describe the old F#-first model).
- Branch: `rewrite/sql-first-dbup`.
- Tests: annotation association, introspection, codegen, runtime helpers, filesystem migrate, view write-op rejection.
- Dogfood: marketbot.

## End-to-end story

1. Author `Migrations/001_....sql` with `CREATE TABLE` / `CREATE VIEW` and `-- mig:` lines.
2. From CLI or `build.fsx`: run codegen → one `{Name}.fs` per relation under the output directory / namespace.
3. At app startup (or script): call `migrateScripts`.
4. Domain code uses `dbTxn` + generated ops; complex filters live as annotated views (read ops only).

## Remaining implementation choices (non-blocking)

- In-memory vs temp file for the codegen DB (temp file is the current choice).
- Exact `CodegenResult` / error DU shapes.
- Whether `select_by_id` is ever valid on views given SQLite PK metadata for views.

## Resolved decisions log

- No generated `Data/` directory; import MigLib.
- Comment style: `-- mig:rel` (optional name override), `-- mig:ops`, column overrides.
- Unannotated relations: no F#.
- No ops ⇒ no type.
- Ops explicit; catalog only; no custom SQL ops (views + `select_by`).
- Column overrides: bool, int/uint/int64, DateTimeOffset (RFC 3339 TEXT).
- Naming: snake_case → PascalCase; SQL ident → type name unless `-- mig:rel Name`.
- Insert inputs: omit **autoincrement only**; keep columns with defaults.
- Upsert: **PK-only** conflict target.
- Output: **one file per relation** in an output directory (`module {namespace}.{Name}`); `--namespace` on CLI; **public codegen + migrate APIs** in MigLib for `build.fsx`.
- Views: **read ops only**; write ops refused at codegen.
- LINQ deferred.
- Transactions: current MigLib model.
- CLI migrate/status out of scope; library filesystem migrate helper yes.
- Schema via temp/in-memory DB + inspect; no full SQL parse.
- No Fantomas in tool.
- Package layout unchanged.
- Migrations run in user app; MigLib helper for ergonomics.
- Generated code depends on MigLib to shrink emission.
- Greenfield replace `src/`.
- marketbot first target.
- Annotated views generate like tables for allowed (read) ops.
