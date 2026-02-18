# Operator Runbook: Hot Migration

This runbook describes how to execute and validate a hot migration using the current `mig` command set.

## Scope

- Source: existing SQLite database (`<dir-name>-<old-hash>.sqlite`)
- Target: new SQLite database generated from `schema.fsx` (`<dir-name>-<schema-hash>.sqlite`)
- Migration mode: online (old service keeps serving reads while migration progresses)

## Preflight Checks

1. Confirm backups exist for the source SQLite file (`<dir-name>-<old-hash>.sqlite`).
2. Confirm the new schema script compiles and reflects correctly (`schema.fsx` in the target directory).
3. Ensure the new service build is ready to run against the inferred target database path.
4. Ensure old and new service instances can be monitored during migration.
5. Ensure disk space is sufficient for a second database file plus migration overhead tables.
6. Run a dry-run plan and resolve blocking issues before migration:

```sh
mig plan [--dir|-d /path/to/project]
```

## Phase 1: Migrate

```sh
mig migrate [--dir|-d /path/to/project]
```

Default no-flag mode (`mig migrate` from project directory):

- uses `./schema.fsx`
- derives target path `./<dir-name>-<schema-hash>.sqlite`
- auto-detects source DB as exactly one `./<dir-name>-<old-hash>.sqlite` file excluding the target
- the same path inference is used by `mig status`, `mig drain`, `mig cutover`, and `mig cleanup-old`
- use `--dir` / `-d` to run the same workflow from a different working directory

Optional metadata:

- `mig migrate` stores git `HEAD` commit metadata automatically in `_schema_identity.schema_commit` when the schema path is inside a git repository

Expected outcomes:

- `new.db` exists and contains the new schema
- old DB has `_migration_marker(status='recording')` and `_migration_log`
- new DB has `_migration_status(status='migrating')`, `_migration_progress`, `_id_mapping`
- bulk copy summary is printed

Validation:

```sh
mig status [--dir|-d /path/to/project]
```

Check that:

- marker status is `recording`
- migration log entries are reported
- migration status is `migrating`
- schema hash is reported for the new database

If the old DB has already been archived and only the inferred new DB remains, `mig status` prints old-side metrics as unavailable (`n/a`) and still reports new DB metadata.

## Deploy New Service (Still Blocked)

Deploy/start the new service pointing at `new.db`.
It should remain blocked from serving while `_migration_status='migrating'`.

## Phase 2: Drain

```sh
mig drain [--dir|-d /path/to/project]
```

Expected outcomes:

- old marker switches to `draining`
- old service rejects writes
- replay consumes pending `_migration_log` entries
- `_migration_progress` is updated with replay checkpoint

Validation:

```sh
mig status [--dir|-d /path/to/project]
```

Check that pending replay entries are `0` before cutover.

If your target schema defines triggers, run a focused replay check:

1. Insert a controlled write into the old DB and append the corresponding `_migration_log` entry.
2. Run `mig drain`.
3. Validate the trigger side effects in the new DB (for example, audit rows/counters written by the trigger).

## Phase 3: Cutover

```sh
mig cutover [--dir|-d /path/to/project]
```

Expected outcomes:

- `_migration_status` becomes `ready`
- replay-only tables `_id_mapping` and `_migration_progress` are removed from `new.db`
- new service can now serve traffic

Validation:

```sh
mig status [--dir|-d /path/to/project]
```

Check that:

- migration status is `ready`
- pending replay entries show `0 (cutover complete)`
- `_id_mapping` and `_migration_progress` are reported as removed

If your target schema defines triggers, run a focused post-cutover write check:

1. Execute a controlled write against the new service/database.
2. Verify trigger side effects are still correct after cutover (same audit/counter expectations used during drain validation).

## Traffic Switch

Move application traffic from old service to new service only after successful cutover validation.

## Optional Phase 4: Cleanup Old DB Migration Tables

```sh
mig cleanup-old [--dir|-d /path/to/project]
```

Expected outcomes:

- `_migration_marker` and `_migration_log` are dropped from old DB
- command reports previous marker status and dropped-table flags

Safety rule:

- cleanup fails if old marker is still `recording`

## Rollback Guidance

Before cutover (`_migration_status='migrating'`):

- keep serving from old service/DB
- restart migration after fixing issues (`drain` can be rerun)

After cutover (`_migration_status='ready'`):

- immediate rollback is operational (route traffic back to old service) but data divergence risk must be evaluated because writes may already be accepted by the new service
- preserve both DB files for investigation before cleanup

After `cleanup-old`:

- migration metadata tables are removed from old DB
- treat rollback as an explicit recovery exercise from backups or retained snapshots

## Failure Triage Pointers

- `migrate failed: ...`
  - read the printed `Recovery snapshot` and `Recovery guidance` blocks
  - keep old DB/service as source of truth; do not run `drain`/`cutover`
  - run `mig plan` after cleanup/reset to verify rerun safety
- `cutover failed: Drain is not complete...`
  - rerun `mig drain`, then recheck `mig status`
- `cleanup-old failed: Old database is still in recording mode...`
  - finish `drain` + `cutover` first
- missing migration tables
  - verify command order (`migrate` -> `drain` -> `cutover`)
