# Program Layers

This directory contains the internal layers behind the `mig` command-line entrypoint.

`../Program.fs` remains the thin entrypoint and dispatch facade. The implementation behind argument parsing, project discovery, and command execution lives here in dependency order.

## Layers

### Args

- define the CLI argument types and subcommands
- keep Argu-specific metadata in one place

### Common

- centralize version rendering, exception formatting, and command result handling
- hold small shared helpers used by the command handlers

### BuildCommands

- implement `codegen` and `init`
- adapt CLI arguments to the `MigLib` workflow functions

### MigrationCommands

- implement `plan`, `migrate`, `status`, and `reset`
- keep CLI-specific output formatting out of `MigLib`

## Dependency Direction

`Args -> Common -> BuildCommands/MigrationCommands -> ../Program.fs`

The CLI should stay thin: `MigLib` owns project discovery, schema loading, and migration behavior.
