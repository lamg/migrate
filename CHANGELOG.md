# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
