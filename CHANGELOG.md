# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
