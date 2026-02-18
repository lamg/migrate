# Operator Runbook: Hot Migration

This runbook describes how to execute and validate a hot migration using the current `mig` command set.

## Scope

- Source: existing SQLite database (`old.db`)
- Target: new SQLite database generated from `schema.fsx` (`new.db`)
- Migration mode: online (old service keeps serving reads while migration progresses)

## Preflight Checks

1. Confirm backups exist for `old.db`.
2. Confirm the new schema script compiles and reflects correctly (same file intended for `mig migrate --schema`).
3. Ensure the new service build is ready to run against `new.db`.
4. Ensure old and new service instances can be monitored during migration.
5. Ensure disk space is sufficient for a second database file plus migration overhead tables.

## Phase 1: Migrate

```sh
mig migrate [--old old.db] [--schema schema.fsx] [--new new.db]
```

Default no-flag mode (`mig migrate` from project directory):

- uses `./schema.fsx`
- derives target path `./<dir-name>-<schema-hash>.sqlite`
- auto-detects source DB as exactly one `./<dir-name>-<old-hash>.sqlite` file excluding the target

Expected outcomes:

- `new.db` exists and contains the new schema
- old DB has `_migration_marker(status='recording')` and `_migration_log`
- new DB has `_migration_status(status='migrating')`, `_migration_progress`, `_id_mapping`
- bulk copy summary is printed

Validation:

```sh
mig status --old old.db --new new.db
```

Check that:

- marker status is `recording`
- migration log entries are reported
- migration status is `migrating`
- schema hash is reported for the new database

## Deploy New Service (Still Blocked)

Deploy/start the new service pointing at `new.db`.
It should remain blocked from serving while `_migration_status='migrating'`.

## Phase 2: Drain

```sh
mig drain --old old.db --new new.db
```

Expected outcomes:

- old marker switches to `draining`
- old service rejects writes
- replay consumes pending `_migration_log` entries
- `_migration_progress` is updated with replay checkpoint

Validation:

```sh
mig status --old old.db --new new.db
```

Check that pending replay entries are `0` before cutover.

## Phase 3: Cutover

```sh
mig cutover --new new.db
```

Expected outcomes:

- `_migration_status` becomes `ready`
- replay-only tables `_id_mapping` and `_migration_progress` are removed from `new.db`
- new service can now serve traffic

Validation:

```sh
mig status --old old.db --new new.db
```

Check that:

- migration status is `ready`
- pending replay entries show `0 (cutover complete)`
- `_id_mapping` and `_migration_progress` are reported as removed

## Traffic Switch

Move application traffic from old service to new service only after successful cutover validation.

## Optional Phase 4: Cleanup Old DB Migration Tables

```sh
mig cleanup-old --old old.db
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

- `cutover failed: Drain is not complete...`
  - rerun `mig drain`, then recheck `mig status`
- `cleanup-old failed: Old database is still in recording mode...`
  - finish `drain` + `cutover` first
- missing migration tables
  - verify command order (`migrate` -> `drain` -> `cutover`)
