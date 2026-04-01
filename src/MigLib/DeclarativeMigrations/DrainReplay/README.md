# DrainReplay Layers

This directory contains the internal layers behind the `Mig.DeclarativeMigrations.DrainReplay` facade.

`../DrainReplay.fs` remains the stable module entrypoint. The implementation is split here by responsibility so parsing, state loading, and replay execution remain separate.

## Layers

### Types

Responsibilities:

- define the internal replay operation and migration log entry shapes
- centralize the shared SQLite error helper used by replay internals

### Parsing

Responsibilities:

- parse `_migration_log` rows from SQLite into typed replay entries
- convert JSON row payloads into declarative migration expressions
- group replay entries by transaction in execution order

This layer depends on `Types`.

### State

Responsibilities:

- load `_id_mapping` state from the target database
- convert replay expressions into database values
- resolve copy-plan mappings and persist updated identity mappings

This layer depends on `Types` and the data-copy domain.

### Execution

Responsibilities:

- execute insert, update, and delete replay operations inside transactions
- orchestrate grouped transaction replay using the parsed entries and loaded ID mappings

This is the top operational layer for replay and depends on all lower layers it needs.

## Dependency Direction

The intended direction is:

`Types -> Parsing/State -> Execution -> ../DrainReplay.fs`

Lower layers should not depend on higher layers. Keep new code in the lowest layer that can own the responsibility cleanly.
