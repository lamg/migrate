# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [6.0.1] - 2026-04-10

Fixed:

- **Deep Transaction Bind Chains**: avoid stack overflows in very large `txn` workflows
  - `TxnStep.bind`, `bindTask`, and `bindTaskResult` now yield before continuing into next step
  - prevents deep recursive transaction pipelines from exhausting stack during large insert workloads
  - adds regression test covering a `50000`-step recursive `txn` chain

## [6.0.0] - 2026-04-10

Changed:

- **Explicit Schema-Bound DB Name Prefix**: code generation no longer derives the SQLite file name prefix from the schema directory
  - `MigLib.Build` now requires callers to pass the desired database/app name prefix when deriving schema-bound database file names
  - compiled-schema `generateDbCode*` and `runCodegen*` APIs now require that prefix explicitly
  - generated `DbFile` values now use the caller-supplied prefix, producing names like `<app-name>-<schema-hash>.sqlite`
  - `mig codegen` and schema-bound path resolution now pass an explicit prefix based on the working directory name

## [5.4.0] - 2026-04-07

Added:

- **Explicit FK Attribute**: foreign keys can now be declared explicitly with the `FK` attribute
  - supports single and multiple columns via ParamArray: `[<FK("user", "userId")>]` or `[<FK("user", "tenantId", "userId")>]`
  - optional `RefColumns` property to specify referenced columns: `[<FK("user", "tenantId", "userId", RefColumns = [|"tenant_id"; "user_id"|])>]`
  - integrates with `OnDeleteCascade` and `OnDeleteSetNull` actions
  - documented in `specs/database_dsl.md` with examples and SQL translations

## [5.3.0] - 2026-04-07

Added:

- **Composite Primary Key Support**: `PK` attributes now support multi-column primary keys
  - declare composite keys with `[<PK("col1", "col2")>]` or repeated `[<PK "col1">] [<PK "col2">]`
  - composite foreign keys expand from record references with deterministic naming: single-column uses `field_id`, composite uses `field_<pkColumn>` for each PK part
  - `OnDeleteCascade` and `OnDeleteSetNull` work at the relationship level for composite FKs
  - seed generation expands nested composite-PK values into all FK columns
  - synthesized views generate multi-column join predicates for composite relationships
  - normalized/DU extension tables support composite FK/PK relationships throughout codegen
  - documented in `specs/database_dsl.md` with examples and SQL translations

## [5.2.9] - 2026-04-03

Added:

- **AOT-Friendly Web JSON Helpers**: `MigLib.Web` now includes reusable `JsonContent` helpers and `Respond.jsonNode` for explicit JSON responses without generic runtime serialization
  - adds `jsonWithTypeInfo` for source-generated or explicit metadata-backed JSON writes
  - adds node/result helpers for strings, numbers, arrays, objects, nullable int64 values, boxed log fields, and common error payloads
  - marks the reflection-based JSON response helpers as not AOT-safe so callers can choose the explicit path deliberately

Changed:

- **MigLib.Web AOT Metadata**: `MigLib.Web` now declares itself trimmable and AOT-compatible
  - includes regression coverage for the explicit `jsonWithTypeInfo` response path

## [5.2.8] - 2026-04-03

Fixed:

- **Generated Result Returns**: code generation now emits valid F# `return Ok ...` and `return Error ...` statements
  - fixes invalid generated output such as `returnOk newId` and `returnError ex` in `Db.fs`
  - adds regression coverage so generated query helpers keep the correct computation-expression syntax

## [5.2.7] - 2026-04-03

Added:

- **Generated DeleteAll Helper**: schemas can now opt into a generated `DeleteAll` method with `[<DeleteAll>]`
  - regular and normalized tables emit `DeleteAll(tx)` as a direct `DELETE FROM <table>` helper
  - view code generation rejects `DeleteAll` explicitly so the read-only surface stays consistent
  - `MigLib` schema reflection, compiled schema serialization, and `mig codegen` all understand the new annotation

## [5.2.6] - 2026-04-02

Changed:

- **AST-Backed Generated Type Definitions**: moved the remaining generated type-definition blocks away from full F# string templates and onto `Fabulous.AST`
  - regular and normalized query members were already emitted through AST builders, and enum types, measure types, and normalized DU property augmentations now follow the same path
  - this reduces handwritten code-string assembly in the generator while preserving the generated API and output verified by the full test suite

## [5.2.5] - 2026-04-02

Changed:

- **Codegen Query Helper Consolidation**: reduced repeated generated database code by pushing more common query and write patterns into `MigLib.Db`
  - added shared helpers for single-row queries, list queries, query-or-insert flows, write execution, insert result handling, upsert control flow, and last-insert-id lookup
  - regular, normalized, and view code generation now reuse common parameter binding and select projection rendering helpers to keep generator output smaller and more consistent
  - preserves generated behavior while simplifying both generated `Db.fs` output and MigLib's code generator internals, validated by the full test suite

## [5.2.4] - 2026-04-02

Fixed:

- **Attribute-Safe RFC3339 Default Expression**: restored `MigLib.Db.Rfc3339UtcNow` as a compile-time literal so schema attributes can reference it directly again
  - `DefaultExpr("createdAt", MigLib.Db.Rfc3339UtcNow)` now compiles without callers needing a local literal alias
  - keeps the public `MigLib.Db` facade aligned with the underlying `DbAttributes.Rfc3339UtcNow` literal contract

## [5.2.3] - 2026-04-01

Changed:

- **MigLib Internal Layering**: split several large implementation files into documented layered directories while keeping their public facades stable
  - `HotMigration`, `HotMigration/Operations`, `SchemaReflection`, `Db`, `DeclarativeMigrations/DrainReplay`, `CodeGen/NormalizedQueryGenerator`, and `CodeGen/QueryGenerator` now separate lower-level helpers from top-level orchestration
  - each refactored area now includes a local `README.md` that explains responsibilities and dependency direction between layers
  - these changes are structural only and preserve the existing runtime and code-generation behavior validated by the test suite
- **mig CLI Internal Layering**: split the CLI entrypoint into documented operational layers under `src/mig/Program/`
  - argument definitions, path/module resolution, build commands, and migration commands are now isolated in separate files under a dedicated directory
  - the public entrypoint and command surface remain stable while the implementation is easier to navigate and evolve

## [5.2.2] - 2026-03-31

Fixed:

- **Packaged F# Runtime Floor**: lowered the packaged `FSharp.Core` dependency to `10.0.104` for `MigLib`, `MigLib.Web`, and `migtool`
  - removes `NU1605` package downgrade warnings when `dotnet fsi` restores build scripts under the .NET `10.0.104` SDK
  - keeps the three published packages aligned on the same packaged F# runtime dependency

## [5.2.1] - 2026-03-30

Fixed:

- **Runtime Assembly Autodiscovery**: `mig init` and the other compiled `Db` commands no longer mistake `Schema.fsproj` for the runtime module assembly
  - `codegen` still prefers `Schema.fsproj` and its compiled schema assembly
  - runtime commands now prefer the non-schema project that actually contains the generated `Db` module
  - when only `Schema.fsproj` is present, `mig` now fails with explicit guidance to pass `--assembly`

## [5.2.0] - 2026-03-30

Added:

- **Compiled Schema Seeds**: compiled schema reflection now derives seed inserts from module-level `let` values whose types are reflected schema record types
  - generated `Db.fs` now preserves those inserts instead of emitting `inserts = []`
  - `init` continues to apply seeds only when creating a fresh target database, and skips reinsertion when the schema-bound database already exists
- **Schema Project Autodiscovery**: `mig` now prefers a dedicated `Schema.fsproj` when inferring the compiled schema assembly
  - honors `<AssemblyName>` when present, so schema-project outputs like `TruthMasker.Schema.dll` are discovered automatically
  - falls back to `Schema.dll`, then to the single-`.fsproj` convention when no dedicated schema project exists
- **Startup Runtime Helper**: `MigLib.HotMigration` now exposes `inferPreviousDatabasePath`
  - the helper inspects the configured SQLite directory, excludes the target `DbFile`, returns the single remaining `*.sqlite` candidate, and fails clearly when the choice is ambiguous

Changed:

- **Service Startup API**: `startService` and `startServiceWithPolling` no longer require a caller-provided previous-database callback
  - the default startup path now uses `MigLib`'s built-in previous-database inference
  - startup integration code is simpler while still failing clearly when multiple old-database candidates exist
- **Documentation**: the README and deploy guidance now recommend the `Schema.fsproj` convention explicitly
  - explains that the separate schema project breaks the build dependency cycle between compiled schema reflection and generated `Db.fs`
  - shows the runtime flow through `MigLib.HotMigration.startService` directly in code

## [5.1.0] - 2026-03-30

Added:

- **MigLib.Build**: added high-level build-facing APIs for compiled-module workflows
  - new codegen entrypoints now return structured reports and shared terminal output lines
  - new init entrypoints resolve the schema-bound `DbFile`, create the database, and report clean skip behavior when the target already exists
- **mig CLI**: `codegen` and `init` now reuse the shared `MigLib.Build` execution and reporting paths
  - keeps CLI output aligned with library consumers instead of maintaining separate formatting logic

Changed:

- **MigLib.Util**: generalized `taskResult` so it works with any error type instead of only `SqliteException`
  - allows build/reporting helpers to compose `Result` and `Task<Result<_, _>>` flows without forcing SQLite-shaped errors
  - preserves the existing usage patterns in hot-migration code while widening the computation expression for other modules

## [5.0.1] - 2026-03-29

Fixed:

- **MigLib / mig CLI**: aligned the packaged F# runtime dependency to `FSharp.Core 11.0.100`
  - fixes compiled-schema loading failures when `mig init`, `migrate`, `offline`, or `plan` reflect over application assemblies built against `FSharp.Core 11`
  - keeps `MigLib`, `MigLib.Web`, and `migtool` on the same F# runtime version for release builds

Changed:

- **mig CLI Local Install Workflow**: `build.fsx` now uninstalls the existing global `migtool` before reinstalling from the freshly packed local package output
  - avoids stale global-tool installs during local release verification
  - ensures `dotnet fsi build.fsx` picks up the newest locally built package artifacts

## [5.0.0] - 2026-03-28

Changed:

- **Schema Source Of Truth**: runtime migration and code generation now use compiled `Schema.fs` / generated `Db.fs` artifacts instead of `.fsx` schema scripts
  - `mig init`, `plan`, `migrate`, `offline`, and path-oriented migration commands now work from compiled generated modules
  - generated `Db.fs` now carries compiled schema descriptors, schema identity metadata, and the schema-bound `DbFile`
  - `mig` and `MigLib` now treat compiled schema/module workflows as the primary model
- **Hot Migration Safety**: automatic migration planning is now stricter and fully explicit for schema renames and source-column drops
  - rename heuristics were removed in favor of `PreviousNameAttribute`
  - dropping source columns on surviving tables requires `DropColumnAttribute`
  - unsupported data-losing transitions now fail clearly during planning instead of being inferred
- **Package Split**: `MigLib.Web` now ships as its own package/project instead of being compiled into `MigLib`
  - `MigLib` no longer carries the ASP.NET Core framework reference
  - consumers that only need the core transaction/runtime APIs can depend on `MigLib` without the web surface
  - ASP.NET Core helpers remain available from the new `MigLib.Web` package
- **Code Generation Ownership**: reusable code generation and hot-migration runtime functionality now live in `MigLib`, with `mig` acting as a CLI wrapper over that surface

Added:

- **Startup Coordination APIs**: `MigLib.Db` now exposes startup decision helpers for `use existing`, `wait`, `migrate this instance`, and `exit early`
- **Compiled Schema Loading**: `MigLib.CompiledSchema` can load generated schema data from compiled modules and drive migration/init operations from assemblies
- **Build API**: `MigLib.Build` now exposes build-facing generation helpers for schema-bound database naming and `Db.fs` generation

Removed:

- **Schema Script Support**: `.fsx` schema parsing/execution has been removed from runtime, code generation, and tests
- **Generated Project Files**: `mig codegen` no longer emits sibling `.fsproj` files
- **FsToolkit.ErrorHandling Dependency**: result and task-result utilities now live in `MigLib.Util`


## [4.1.4] - 2026-03-21

Fixed:

- **mig CLI Error Reporting**: Command failures now include full exception chain details instead of only top-level messages
  - `init`, `migrate`, `offline`, `plan`, `drain`, `cutover`, `archive-old`, `reset`, and `status` now render inner exception context
  - set `MIG_DEBUG=1` to include full exception text (with stack trace) in CLI failure output

## [4.1.3] - 2026-03-13

Changed:

- **MigLib Runtime**: New-service transactions now enforce `_migration_status` before serving traffic
  - blocks reads and writes while the target database is still `migrating`
  - allows normal traffic once the target database reports `ready`
- **mig CLI / Code Generation**: `mig codegen` now emits a schema-bound `DbFile` literal for generated modules
  - generated services can bind directly to `<dir-name>-<schema-hash>.sqlite`
  - release docs and operator guidance now describe explicit schema-specific database paths during online migration

## [4.1.2] - 2026-03-13

Fixed:

- **MigLib SQLite Initialization**: `openSqliteConnection` and read-only connection helpers now initialize `SQLitePCL.Batteries_V2` explicitly before opening SQLite
  - fixes `mig init` failures in packaged/global-tool executions where SQLite provider initialization was not happening reliably
  - keeps the initialization idempotent through a single lazy guard inside `MigLib.Db`

## [4.1.1] - 2026-03-13

Fixed:

- **MigLib.Web**: Added explicit `WebOp` return signatures to `Respond.json` and `Respond.jsonWith`
  - prevents F# consumers from widening queued JSON response operations to `WebOp<obj, obj, obj, unit>`
  - restores clean composition with `webResult`, `Web.ofTxnAppResult`, and typed response helpers
- **Tests**: Regression coverage now exercises `Respond.json` after `Web.ofTxnAppResult`

## [4.1.0] - 2026-03-13

Added:

- **MigLib.Web**: Added direct `webResult` support for `TxnStep<Result<'a, 'appError>>`
  - new `Web.ofTxnAppResult` helper flattens transaction-scoped app results into `WebOp`
  - new public `Web.ofAppResult` and `Web.ofAppTaskResult` helpers expose the existing app-result conversions
  - new `Web.ignore` helper makes it easier to discard successful values in request flows
- **Tests**: Added regression coverage for `TxnStep<Result<_, _>>` binding inside `webResult`

## [4.0.0] - 2026-03-13

Changed:

- **mig CLI**: Renamed the old-database finalization command from `cleanup-old` to `archive-old`
  - `mig offline` now archives the source database into `archive/` after a successful copy
  - archived old databases replace any same-named prior file in the project-local `archive/` directory
  - command help, operational docs, and workflow specs now use the new archival terminology consistently
- **Code Generation**: `mig codegen` now always emits a sibling F# project file next to the generated source
  - generated `.fsproj` files keep package references versionless for Central Package Management via `Directory.Packages.props`
- **MigLib Runtime**: Renamed the old-database archival API surface to match the new operation naming
  - `runArchiveOld` now moves the old database file into `archive/`
  - archival result metadata now reports `archivePath` and whether an existing archive was replaced

## [3.0.0] - 2026-03-07

Changed:

- **Schema Source Of Truth**: Replaced the previous SQL-first workflow with reflection over `schema.fsx`
  - schema hashing now drives deterministic database naming
  - schema metadata is persisted in `_schema_identity`
- **Hot Migration Workflow**: Introduced online migration orchestration with `mig plan`, `migrate`, `drain`, `cutover`, `cleanup-old`, `reset`, and `status`
  - bulk copy is FK-aware and records `_id_mapping`
  - drain replay uses `_migration_log` and `_migration_progress`
  - cutover validates replay safety before marking the target database `ready`
- **Library Transaction API**: Split the transaction surface into `dbTxn` and reusable `txn`
  - `dbTxn` now resolves hash-template database paths such as `<HASH>` before opening SQLite
  - migration write recording/drain gating is integrated into the runtime transaction flow
- **Code Generation**: `mig codegen` now generates F# query helpers directly from `schema.fsx`
  - invalid module names or unparsable generated code now fail the command instead of writing broken output
- **Package Surface**: Slimmed `MigLib` down to runtime dependencies only
  - schema reflection, migration orchestration, and code generation now ship with the `mig` tool instead of the `MigLib` NuGet package

## [2.13.0] - 2026-02-11

Added:

- **QueryLike Annotation**: New `QueryLike(column)` annotation for generated `%value%` SQL search methods
  - Generates methods named `GetBy{Column}Like`
  - Uses SQL `WHERE column LIKE '%' || @column || '%'`
  - Supported for regular tables, normalized tables, and views
  - Validates that exactly one existing column is specified

## [2.12.1] - 2026-02-11

Fixed:

- **Seed Execution FK Handling**: Disable foreign key checks while running `mig seed`, then re-enable them after commit
  - Prevents failures when seed data for related tables must be inserted in an order that temporarily violates FK constraints
  - Added regression test coverage for cyclic foreign key seed scenarios

## [2.12.0] - 2026-02-11

Changed:

- **SQL Parser Migration**: Replaced the FParsec-based SQLite schema parser with FsLexYacc
  - Added lexer/parser grammar sources (`SqlLexer.fsl`, `SqlParser.fsy`) and generated parser modules
  - Preserved existing migration/test behavior while removing FParsec dependency

Fixed:

- **Foreign Key Actions**: Properly parse and generate `ON DELETE`/`ON UPDATE` actions, including `ON DELETE CASCADE`
  - Inline column foreign keys now emit valid `REFERENCES ... ON DELETE ...` SQL during table recreation
  - Constraint-only FK changes now trigger table recreation migrations
- **View Dependencies During Table Recreation**: Migration ordering now drops dependent views before recreated tables and recreates them afterward
  - Avoids SQLite failures when recreating tables referenced by existing views
  - Keeps `PRAGMA foreign_keys=OFF/ON` in the correct outer envelope during execution

## [2.11.0] - 2026-02-10

Added:

- **TaskTxnBuilder loop support**: Added `For`, `Zero`, `Delay`, and `Combine` members to `TaskTxnBuilder`, enabling `for` loops inside `taskTxn` computation expressions
  - All iterations share a single transaction, improving performance for batch operations
  - Stops on first error and rolls back the entire transaction

## [2.9.1] - 2026-02-06

Fixed:

- **Version Synchronization**: Synced MigLib and migtool package versions for this patch release

## [2.9.0] - 2026-02-05

Changed:

- **InsertOrIgnore Annotation Rename**: Renamed the table annotation from `IgnoreNonUnique` to `InsertOrIgnore` to better reflect the generated `InsertOrIgnore` method

## [2.8.0] - 2026-02-02

Added:

- **QueryByOrCreate on Extension-Only Fields**: QueryByOrCreate annotations can now reference columns that exist only in extension tables of normalized schemas
  - When a DU case doesn't have the required query columns, generates `invalidArg` to throw at runtime
  - Only DU cases with all required columns can be used with the generated method
  - Example: `QueryByOrCreate(department_id, employee_id)` on a `person` table works when those columns are in `person_employment` extension

## [2.7.2] - 2026-02-02

Fixed:

- **DEFAULT Expression Parsing**: Fixed parser to handle parenthesized DEFAULT expressions with nested function calls
  - Expressions like `DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now', 'utc'))` now parse correctly
  - Parser properly balances parentheses and preserves quoted strings inside expressions

## [2.7.1] - 2026-02-02

Fixed:

- **QueryByOrCreate Code Generation**: Fixed indentation bug in async value extraction for normalized tables with multiple extensions
  - Async methods now correctly indent `let` bindings (8 spaces in `task { try` blocks)
  - Sync methods correctly indent `let` bindings (6 spaces in `try` blocks)
- **Annotation Parser Order**: Fixed parser to handle `-- QueryBy` and `-- QueryByOrCreate` annotations in any order
  - Previously, `-- QueryByOrCreate` annotations appearing before `-- QueryBy` would prevent the latter from being parsed
  - Now both annotation types are parsed correctly regardless of their order in the SQL file

## [2.7.0] - 2026-02-01

Changed:

- **Complete Fabulous.AST Migration**: All async code generation now uses AST-based generation
  - 11 async methods migrated from string templates to Fabulous.AST
  - Consistent patterns using `taskExpr` and `trySqliteExceptionAsync` helpers
  - While loop pattern for async reader operations
  - Only complex methods (QueryByOrCreate async, normalized with extensions) remain as string templates

Internal:

- Improved code maintainability with unified AST generation approach
- Better separation between sync and async code patterns in AstExprBuilders.fs

## [2.6.0] - 2026-01-30

Added:

- **Async Code Generation**: New `--async` flag for `mig codegen` command
  - Generates Task-based async CRUD methods alongside sync methods
  - All async operations use `task { }` computation expressions
  - Proper async/await patterns with `use!`, `let!`, and `return`

## [2.5.0] - 2026-01-28

Added:

- **Fantomas Integration**: Automatic code formatting for generated F# code
  - 2-space indentation for consistent style
  - Proper formatting of match expressions and record literals
- **Named Tuple Fields**: DU type generation uses named tuples instead of anonymous records
  - Better IntelliSense support and field documentation

Fixed:

- Command variable names in generated parameter bindings
- DU match expressions to use named field patterns
- Commas between named tuple fields in GetAll/GetById/GetOne methods

## [2.4.0] - 2026-01-20

Changed:

- **Central Package Management (CPM)**: Generated projects now use CPM
  - Package versions managed via `Directory.Packages.props`
  - Simplified package reference syntax in generated `.fsproj` files

Fixed:

- Code generation indentation issues

## [2.3.0] - 2026-01-16

Added:

- **QueryByOrCreate Annotation**: New annotation for get-or-create patterns
  - Add `-- @QueryByOrCreate column1,column2` annotations above table definitions
  - Generates methods that return existing record or insert new one
  - Atomic operation within transaction

Changed:

- **Simplified Db.txn API**: Streamlined transaction API for easier use

## [2.2.0] - 2026-01-14

Added:

- **Seed Statement Support**: New `mig seed` command for database seeding with INSERT OR REPLACE upserts
  - Allows declarative seed data in migration files
  - Automatically handles conflicts using INSERT OR REPLACE
- **QueryBy Annotation**: Custom query generation feature for tables
  - Add `-- @QueryBy column1,column2` annotations above table definitions
  - Generates custom query methods like `GetByColumn1AndColumn2`
  - Supports both regular and normalized schema tables

Changed:

- **Version Alignment**: MigLib version bumped to 2.2.0 to match migtool CLI version
  - Ensures consistent feature set across library and CLI tool
  - Both packages now reflect the same capabilities

## [2.0.0] - 2025-01-13

Added:

- **Functional Type Relational Mapping (FTRM)**: Introduced FTRM as the functional programming paradigm for mapping database relations to functional types, serving as the F# equivalent to Object-Relational Mapping
- **Normalized Schema Support**: Complete implementation of normalized (2NF) schema representation using discriminated unions instead of option types
  - Automatic detection of extension tables via naming convention (`{base_table}_{aspect}`)
  - Generation of two discriminated union types per normalized table: `New{Type}` for inserts and `{Type}` for queries
  - Pattern matching-based CRUD operations with transaction atomicity
  - Convenience properties exposing all fields across union cases with optional typing for partial fields
  - Comprehensive validation with actionable error messages
- **Code Generation Statistics**: Display of normalized vs regular table counts in `mig codegen` output

Changed:

- **API Enhancement**: All CRUD operations now support normalized schemas with discriminated unions
- **Type Safety**: Enhanced type generation with patterns for both normalized and regular tables
- **Documentation**: Comprehensive specification documenting FTRM principles and normalized schema feature

Fixed:

- Improved error handling for schema validation with clear, actionable suggestions

## [1.0.4] - 2025-03-03

Fixed:

- Version numbers

## [1.0.3] - 2025-03-03

Added:

- Support for multiple column UNIQUE and PRIMARY KEY constraints
- Environment variable `migrate_db` sets the default database path, falling back to `<CURRENT_DIR_NAME>.sqlite` if not defined

## [1.0.2] - 2025-01-11

Changed:

- migrate as library

## [1.0.1] - 2024-11-26

Added:

- `-v` flag to print the command version

## [1.0.0] - 2024-11-26

Full rewrite

## [0.0.19] - 2024-06-28

Fixed:

- fixed `selectAll` function generation: snake_case to camelCase is now being applied

## [0.0.18] - 2024-06-28

Added:

- Basic SQL type checking for project schema
- F# project generation with `selectAll` queries for all views and tables

Fixed:

- `mig relations` command.
- `mig log` command when SQLite file does not exists
