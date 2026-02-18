# mig command specification

Every migration creates a fresh database from the .fsx specification and copies data from the old one. There is no incremental migration state to track — the .fsx file is always the complete source of truth. This means there is no need for a migration history table (`_schema_migration`) or a `mig log` command.

## Architecture

Two components coordinate during migration:

- **mig** (CLI tool): run by the administrator to drive migration phases
- **MigLib** (library): embedded in both old and new services via the `taskTxn` CE, reacts to markers in the database

They communicate through marker tables in the databases:

- `_migration_marker` in the **old database**: read by MigLib in the old service
  - `status = 'recording'` → MigLib records all writes to `_migration_log`
  - `status = 'draining'` → MigLib stops accepting writes
- `_migration_status` in the **new database**: read by MigLib in the new service
  - `status = 'migrating'` → MigLib rejects all requests
  - `status = 'ready'` → MigLib starts serving
- `_migration_progress` in the **new database**: replay checkpoint state for drain safety and status reporting
- `_schema_identity` in the **new database**: persisted schema metadata (`schema_hash`, optional `schema_commit`, creation timestamp)

## Offline mode

When no service is deployed, the entire migration can be performed in a single command:

```
mig migrate [--dir|-d /path/to/project]
```

This command:

1. Evaluates `schema.fsx` and derives the new schema via reflection
2. Creates a new SQLite file with deterministic name `<dir-name>-<schema-hash>.sqlite`
3. Diffs the old and new schemas to derive column mapping
4. Copies all data from the old database to the new one in FK dependency order, building the `_id_mapping` table
5. Drops `_id_mapping` from the new database
6. Reports the result

No recording, replay, or drain phases are needed since no service is writing to the old database. After completion the administrator swaps the database files.

## Online mode

When services are deployed, the administrator uses three required phase commands plus one optional cleanup command:

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
4. MigLib in the old service detects the marker and begins recording all writes to `_migration_log` at the `taskTxn` CE level
5. Bulk-copies data from old to new in FK dependency order, building `_id_mapping`
6. Exits and reports the result

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
3. Replays all `_migration_log` entries accumulated since the bulk copy, translating IDs through `_id_mapping`
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

- Verifies that drain is complete (`_migration_log` fully consumed)
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
- **`status = 'recording'`**: the `taskTxn` CE wraps every write operation to also insert an entry into `_migration_log` with `txn_id`, `ordering`, operation type, table name, and row data (including assigned autoincrement IDs). Read operations are unaffected.
- **`status = 'draining'`**: the `taskTxn` CE rejects write operations and returns unavailable. Read operations continue normally.

### New service

MigLib in the new service checks the `_migration_status` table on startup and periodically:

- **`status = 'migrating'`**: all requests (reads and writes) are rejected
- **`status = 'ready'`**: normal operation

## Summary

| Command | What it does | Who reacts |
|---|---|---|
| `mig migrate` | Creates new DB, copies data, exits | Old service starts recording writes |
| `mig drain` | Sets drain marker, replays all accumulated writes, exits | Old service stops writes |
| `mig cutover` | Sets ready marker, removes replay-only tables | New service starts serving |
| `mig cleanup-old` | Removes old migration tables from archived old DB | — |
| `mig status` | Shows migration progress | — |
