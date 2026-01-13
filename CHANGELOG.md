# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
