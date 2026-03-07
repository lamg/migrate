# mig command specification

Every migration creates a fresh database from the .fsx specification and copies data from the old one. There is no incremental migration state to track — the .fsx file is always the complete source of truth. This means there is no need for a migration history table (`_schema_migration`) or a `mig log` command.

## Architecture

Two components coordinate during migration:

- **mig** (CLI tool): run by the administrator to drive migration phases
- **MigLib** (library): embedded in old and new services via the `dbTxn` CE; reusable transaction-scoped helpers can be composed with `txn`

They communicate through marker tables in the databases:

- `_migration_marker` in the **old database**: read by MigLib in the old service
  - `status = 'recording'` → MigLib records all writes to `_migration_log`
  - `status = 'draining'` → MigLib stops accepting writes
- `_migration_status` in the **new database**: read by MigLib in the new service
  - `status = 'migrating'` → MigLib rejects all requests
  - `status = 'ready'` → MigLib starts serving
- `_migration_progress` in the **new database**: replay checkpoint state for drain safety and status reporting
- `_schema_identity` in the **new database**: persisted schema metadata (`schema_hash`, optional `schema_commit`, creation timestamp)

## Service DB path resolution

MigLib accepts either an explicit SQLite file path or a hash-template path containing `<HASH>`.
When a template is used, MigLib applies these rules:

1. If exactly one matching file exists (`<prefix><16-hex><suffix>`), use it.
2. If multiple matching files exist and exactly one has `_migration_status='ready'`, use that file.
3. Otherwise resolution fails and the service must be configured with an explicit path.

In practice, hash-template paths are useful for steady-state deployments after cutover. During `migrate` and `drain`, when both old and new databases may coexist and neither is uniquely `ready`, the blocked new service should use the explicit inferred target path instead of a template.

## Schema bootstrap and code generation

### `mig init`

```sh
mig init [--dir|-d /path/to/project]
```

Schema-only bootstrap mode (no source database required):

- Uses the current directory as project root (override with `--dir` / `-d`)
- Uses `<dir>/schema.fsx` as schema input
- Derives target path as `<dir>/<dir-name>-<schema-hash>.sqlite`
- If the schema-matched target already exists, reports skip and exits `0`

Behavior:

1. Evaluates `schema.fsx` and derives schema + seed inserts via reflection
2. Creates a new SQLite file at the deterministic target path
3. Creates all tables, indexes, views, and triggers
4. Applies schema seed inserts in dependency order
5. Reports completion and seeded row count

This command does not create migration coordination tables (`_migration_status`, `_migration_progress`, `_id_mapping`, `_migration_marker`, `_migration_log`) because it is not a hot-migration phase.

### `mig codegen`

```sh
mig codegen [--dir|-d /path/to/project] [--module|-m Schema] [--output|-o Schema.fs]
```

Schema/code generation mode (no database mutations):

- Uses the current directory as project root (override with `--dir` / `-d`)
- Uses `<dir>/schema.fsx` as schema input
- Uses `Schema` as the default generated module name (override with `--module` / `-m`)
- Uses `Schema.fs` as the default output file name (override with `--output` / `-o`)
- Requires the output to be a file name in the same directory as `schema.fsx` (no absolute paths or subdirectories)

Behavior:

1. Evaluates `schema.fsx` and reflects tables, normalized DU tables, views, and query annotations
2. Generates formatted F# source for the reflected types and query helpers
3. Writes the generated module to the requested output file
4. Reports counts for normalized tables, regular tables, and views

## Online mode

When services are deployed, the administrator can run one optional planning command, then three required phase commands plus optional cleanup/reset commands:

### `mig plan`

```sh
mig plan [--dir|-d /path/to/project]
```

Dry-run planning mode (no database mutations):

- Uses the current directory as project root (override with `--dir` / `-d`)
- Uses `<dir>/schema.fsx` as schema input
- Derives target path as `<dir>/<dir-name>-<schema-hash>.sqlite`
- Auto-detects source DB as exactly one `<dir>/<dir-name>-<old-hash>.sqlite` file excluding the target path

If the schema-matched target database already exists and no source candidate is found, plan is skipped as a no-op.

Outputs:

- Inferred old/schema/new paths
- Schema hash and optional schema commit metadata
- Planned table copy order
- Supported vs unsupported schema differences (including target non-table consistency checks)
- Replay prerequisites (`_migration_marker`, `_migration_log`, target-path availability)
- `Can run migrate now: yes|no`

Exit code:

- `0` when migrate can run with current inferred inputs
- `1` when blocking preflight issues are detected

### `mig migrate`

```
mig migrate [--dir|-d /path/to/project]
```

Default behavior:

- Uses the current directory as project root (override with `--dir` / `-d`)
- Uses `<dir>/schema.fsx` as schema input
- Derives target path as `<dir>/<dir-name>-<schema-hash>.sqlite`
- Auto-detects source DB as exactly one `<dir>/<dir-name>-<old-hash>.sqlite` file excluding the target path

If the schema-matched target database already exists and no source candidate is found, migrate is skipped as a no-op.

1. Evaluates `schema.fsx` and derives the new schema via reflection
2. Creates the new SQLite file with the new schema, `_id_mapping`, `_migration_status(status='migrating')`, and `_schema_identity`
3. Writes `_migration_marker(status='recording')` and `_migration_log` to the old database
4. MigLib in the old service detects the marker and begins recording all writes executed inside `dbTxn` transactions into `_migration_log`
5. Bulk-copies data from old to new in FK dependency order, building `_id_mapping`
6. Exits and reports the result

If migrate fails after partial setup, the CLI prints a recovery snapshot (old/new migration artifacts and status) plus rerun guidance so operators can safely reset before retrying.

After this command the old service continues operating normally while recording writes. The administrator can deploy the new service pointing at the new database at any time — MigLib in the new service reads `_migration_status(status='migrating')` and rejects all requests until cutover. The administrator can use `mig status` to monitor how many writes have accumulated in the migration log before triggering drain.

### `mig drain`

```
mig drain [--dir|-d /path/to/project]
```

Default behavior:

- Uses the current directory as project root (override with `--dir` / `-d`)
- Derives target path as `<dir>/<dir-name>-<schema-hash>.sqlite` from `<dir>/schema.fsx`
- Auto-detects source DB as exactly one `<dir>/<dir-name>-<old-hash>.sqlite` file excluding the target path

1. Updates `_migration_marker` status to 'draining' in the old database
2. MigLib in the old service detects the status change and stops accepting writes. Read queries continue to be served from the old database.
3. Replays `_migration_log` entries beyond the current `_migration_progress.last_replayed_log_id` checkpoint (initialized to `0` during `mig migrate`), translating IDs through `_id_mapping`
4. Waits until the log is fully consumed
5. Reports "Drain complete. Run `mig cutover` when ready." and exits

Only writes are unavailable between drain and cutover. This window depends on how many writes accumulated since `mig migrate` finished.

### `mig cutover`

```
mig cutover [--dir|-d /path/to/project]
```

Run after `mig drain` has exited:

- Uses the current directory as project root (override with `--dir` / `-d`)
- Derives target path as `<dir>/<dir-name>-<schema-hash>.sqlite` from `<dir>/schema.fsx`

- Verifies that drain is complete from `_migration_progress`; when the old DB can also be inferred, re-checks old-db replay safety (`_migration_marker='draining'`, `_migration_log` present, no log entries beyond the replay checkpoint)
- Drops replay-only tables (`_id_mapping`, `_migration_progress`) from the new database
- Updates `_migration_status` to 'ready' in the new database
- MigLib in the new service detects the status change and starts serving
- Reports completion

The administrator then switches traffic from the old service to the new one.
Old migration tables (`_migration_marker`, `_migration_log`) are retained until the old database is archived/deleted.

### `mig cleanup-old`

```
mig cleanup-old [--dir|-d /path/to/project]
```

Optional command for archived environments, run after traffic has moved to the new service:

- Uses the current directory as project root (override with `--dir` / `-d`)
- Auto-detects source DB as exactly one `<dir>/<dir-name>-<old-hash>.sqlite` file (excluding inferred current-schema target when available)

- Validates the old database is not still in `_migration_marker(status='recording')`
- Drops old migration tables (`_migration_marker`, `_migration_log`) when present
- Reports previous marker status and cleanup actions

The command is idempotent: if migration tables are already gone, it succeeds and reports no-op cleanup.

### `mig reset`

```sh
mig reset [--dir|-d /path/to/project] [--dry-run]
```

Optional command for failed/aborted migrations before cutover:

- Uses the current directory as project root (override with `--dir` / `-d`)
- Uses `<dir>/schema.fsx` to infer target path `<dir>/<dir-name>-<schema-hash>.sqlite`
- Auto-detects source DB as exactly one `<dir>/<dir-name>-<old-hash>.sqlite` file excluding the inferred target

Behavior:

- Drops old migration tables (`_migration_marker`, `_migration_log`) when present
- Deletes inferred new database file when present and not `ready`
- Refuses reset when inferred new database has `_migration_status='ready'`
- Reports previous old marker status plus new-db deletion outcome
- With `--dry-run`, prints the same inferred impact (`would drop`, `would delete`, blocking reason) without mutating old/new artifacts; exits `0` when reset is actionable and `1` when blocked.

Use `mig reset` to return to a clean pre-migration state after a failed migrate attempt.

### `mig status`

```
mig status [--dir|-d /path/to/project]
```

Shows the current migration state:

- Uses the current directory as project root (override with `--dir` / `-d`)
- Auto-detects source DB as exactly one `<dir>/<dir-name>-<old-hash>.sqlite` file (excluding inferred new target when present)
- Infers new DB as `<dir>/<dir-name>-<schema-hash>.sqlite` from `<dir>/schema.fsx` and includes it only when that file exists
- If old DB cannot be inferred but inferred new DB exists, status falls back to new-only inspection (pending replay/old marker metrics become unavailable)

- Marker status in old database (recording, draining, or no marker)
- Number of entries in `_migration_log`
- Number of entries pending replay (when both old and new DB are available)
- `_id_mapping` state (entry count or removed)
- `_migration_progress` state (present or removed)
- Migration status in new database (migrating, ready)
- Schema identity in new database (schema hash and optional schema commit)

## MigLib behavior

### Old service

MigLib in the old service periodically checks for the `_migration_marker` table:

- **No marker**: normal operation
- **`status = 'recording'`**: `dbTxn` records every committed write operation into `_migration_log` with `txn_id`, `ordering`, operation type, table name, and row data (including assigned autoincrement IDs). Read operations are unaffected.
- **`status = 'draining'`**: `dbTxn` rejects write operations and returns unavailable. Read operations continue normally.

### New service

MigLib in the new service checks the `_migration_status` table on startup and periodically:

- **`status = 'migrating'`**: all requests (reads and writes) are rejected
- **`status = 'ready'`**: normal operation

## Summary

| Command | What it does | Who reacts |
|---|---|---|
| `mig init` | Creates a schema-matched database from `schema.fsx` + seed data (no source DB) | — |
| `mig codegen` | Generates F# query helpers from `schema.fsx` into a source file | — |
| `mig plan` | Prints dry-run migration plan and prerequisites without mutating DBs | — |
| `mig migrate` | Creates new DB, copies data, exits | Old service starts recording writes |
| `mig drain` | Sets drain marker, replays all accumulated writes, exits | Old service stops writes |
| `mig cutover` | Sets ready marker, removes replay-only tables | New service starts serving |
| `mig cleanup-old` | Removes old migration tables from archived old DB | — |
| `mig reset` | Clears failed-migration artifacts (old marker/log + non-ready target DB), or reports dry-run impact with `--dry-run` | — |
| `mig status` | Shows migration progress | — |
