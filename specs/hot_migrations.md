## Hot migrations

Every migration creates a fresh database from the .fsx specification and copies data from the old one. There are no incremental schema changes applied in place, so there is no need to track migration history — the .fsx file is always the complete source of truth.

The migration has three phases, each triggered by a `mig` CLI command (see `mig_command.md`). MigLib, embedded in both old and new services, reacts to marker tables in the databases.

### Migration tables

In the **new database**:

```sql
CREATE TABLE _id_mapping(
  table_name TEXT NOT NULL,
  old_id INTEGER NOT NULL,
  new_id INTEGER NOT NULL,
  PRIMARY KEY (table_name, old_id)
);

CREATE TABLE _migration_status(
  id INTEGER PRIMARY KEY CHECK (id = 0),
  status TEXT NOT NULL  -- 'migrating', 'ready'
);

CREATE TABLE _migration_progress(
  id INTEGER PRIMARY KEY CHECK (id = 0),
  last_replayed_log_id INTEGER NOT NULL,
  drain_completed INTEGER NOT NULL
);
```

In the **old database**:

```sql
CREATE TABLE _migration_marker(
  id INTEGER PRIMARY KEY CHECK (id = 0),
  status TEXT NOT NULL  -- 'recording', 'draining'
);

CREATE TABLE _migration_log(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  txn_id INTEGER NOT NULL,
  ordering INTEGER NOT NULL,
  operation TEXT NOT NULL,  -- 'insert', 'update', 'delete'
  table_name TEXT NOT NULL,
  row_data TEXT NOT NULL    -- JSON with column-value pairs
);
```

### Phase 1: Migrate (`mig migrate`)

1. `mig` creates the new SQLite file with the new schema, `_id_mapping`, and `_migration_status(status='migrating')`
2. `mig` writes `_migration_marker(status='recording')` and `_migration_log` to the old database
3. MigLib in the old service detects the marker and enters recording mode. The `taskTxn` CE transparently records every write operation into `_migration_log` with:
   - `txn_id`: groups operations that belong to the same transaction
   - `ordering`: preserves ordering of operations within a transaction
   - `operation`: insert, update, or delete
   - `table_name`: the target table
   - `row_data`: JSON with column-value pairs, including the assigned autoincrement ID for inserts
4. `mig` notes the current `_migration_log.id` as the cutoff point
5. `mig` bulk-copies data from old to new in FK dependency order. For each row copied, the mapping from old ID to new ID is recorded in `_id_mapping`. Foreign key columns are translated using `_id_mapping` for already-copied parent tables. Column mapping for schema changes (renames, additions with defaults, removals) is derived automatically from the DSL diff between the old and new .fsx specifications.
6. `mig` exits

The old service continues operating normally while recording writes. The new service can be deployed at any time — MigLib reads `_migration_status(status='migrating')` and rejects all requests until cutover.

### Phase 2: Drain (`mig drain`)

1. `mig` updates `_migration_marker` status to 'draining' in the old database
2. MigLib in the old service detects the status change and stops accepting writes, returning unavailable to write requests. Read queries continue to be served from the old database.
3. `mig` replays all `_migration_log` entries accumulated since the bulk copy cutoff point, grouped by `txn_id` and ordered by `ordering`. For each operation:
   - **Insert**: insert the row into the new database. The old autoincrement ID from `row_data` is recorded in `_id_mapping` alongside the new ID assigned by the new database. Foreign key columns are translated through `_id_mapping`.
   - **Update**: translate the primary key and any foreign key columns through `_id_mapping`, then apply the update.
   - **Delete**: translate the primary key through `_id_mapping`, then apply the delete.
   - Each `txn_id` group is applied as a single transaction to preserve atomicity. `mig` knows which columns are foreign keys from the DSL type definitions, so it knows exactly which values need translation.
4. `mig` waits until `_migration_log` is fully consumed, then exits

Only writes are unavailable between drain and cutover. This window depends on how many writes accumulated since `mig migrate` finished.

### Phase 3: Cutover (`mig cutover`)

1. `mig` verifies that drain is complete (`_migration_log` fully consumed)
2. `mig` drops replay-only tables (`_id_mapping`, `_migration_progress`) from the new database
3. `mig` updates `_migration_status` to 'ready' in the new database
4. MigLib in the new service detects the status change and starts serving
5. The administrator switches traffic from old to new
6. Old database migration tables (`_migration_marker`, `_migration_log`) are retained until the old database is archived/deleted

### Phase 4 (optional): Old DB cleanup (`mig cleanup-old`)

1. `mig` verifies the old marker is not still in `recording` mode
2. `mig` drops old migration tables (`_migration_marker`, `_migration_log`) from the old database
3. `mig` reports cleanup actions and exits

This step is intended for archived environments after traffic has already moved to the new service. It is idempotent.

## Alternative approaches

### In-place expand-contract (WAL mode)

No new database file. Modify the schema in place using SQLite's WAL mode.

1. Enable WAL mode
2. Expand: add new tables/columns (readers unaffected, writers briefly blocked)
3. Deploy new code that reads from both old+new, writes to new
4. Backfill data from old columns/tables to new
5. Contract: drop old columns/tables in a later deployment

Pros:

- No data copy for additive changes — simplest operationally
- Single database file, no coordination between two files
- Well-understood pattern (Rails, Django, etc.)
- No migration log, no replay, no drain phase for additive changes

Cons:

- Table recreation (needed for column renames/type changes in SQLite) takes a write lock proportional to table size
- Requires multiple deployments for a single logical migration (expand, deploy new code, contract)
- No clean rollback mid-contract — if the DROP fails partway, you're in an inconsistent state
- Cannot handle migrations that fundamentally restructure data (e.g., splitting a table into two)

### Dual-write

Instead of recording writes for later replay, the old service writes to both databases simultaneously.

1. Create new DB with new schema
2. Bulk copy old → new
3. Signal old service to dual-write (every write goes to both old and new DB with column mapping)
4. Once bulk copy catches up, drain + cutover

Pros:

- New DB is always nearly up to date — less replay lag
- Cutover is faster since there's less to catch up on

Cons:

- Old service must know the new schema and column mapping to write correctly
- Write latency doubles during migration
- If one write succeeds and the other fails, the databases diverge — need distributed transaction semantics for a single-process embedded database, which is awkward
- More invasive change to the old service code

### File swap with brief pause

Prepare the new database completely offline, then swap.

1. Pause the service (return 503 to all requests)
2. Copy old DB file
3. Run schema migration on the copy
4. Rename new file into place
5. Resume service

Pros:

- By far the simplest to implement — no migration log, no replay, no coordination
- Atomic — either it works or you keep the old file
- Trivial rollback (keep old file, rename back)
- No risk of data divergence

Cons:

- Full downtime (reads and writes) during copy + migration
- Downtime proportional to database size — unacceptable for large databases
- Not truly "hot"

### Proxy-based query translation

A migration-aware layer between the application and SQLite translates queries between old and new schemas on the fly.

1. Deploy the translation layer
2. Apply new schema to the database
3. Translation layer rewrites old-format queries to work on the new schema
4. Deploy new code, translation layer becomes a pass-through
5. Remove translation layer

Pros:

- Zero downtime — no drain phase at all
- No dual-write, no replay
- Old and new code can run simultaneously against the same database

Cons:

- Query translation is extremely hard to get right for all SQL patterns
- Significant implementation and debugging complexity
- Performance overhead on every query
- SQLite is typically in-process, so adding a proxy changes the architecture fundamentally

## Comparison

| | Record+Replay | In-place expand-contract | Dual-write | File swap | Proxy |
|---|---|---|---|---|---|
| Write downtime | Brief (drain) | None for additive, lock for recreation | Brief (drain) | Full | None |
| Read downtime | None | None | None | Full | None |
| Implementation complexity | Medium | Low | High | Very low | Very high |
| Rollback safety | Good (old file intact) | Poor (in-place changes) | Poor (two diverged DBs) | Excellent | Poor |
| Handles all schema changes | Yes | Limited by ALTER TABLE | Yes | Yes | Depends on translator |
| Old service modifications | Records writes | None (schema-level only) | Dual-write logic | None | None |
| Data size sensitivity | Copy time grows | Lock time grows for recreation | Copy time grows | Downtime grows | None |
