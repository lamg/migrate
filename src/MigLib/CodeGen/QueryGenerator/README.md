# QueryGenerator Layers

This directory contains the internal layers behind the `Mig.CodeGen.QueryGenerator` facade.

`../QueryGenerator.fs` remains the stable facade and re-exports the entry points used by code generation orchestration.

## Layers

### Common

Responsibilities:

- provide shared helpers for indentation, primary-key and foreign-key inspection, row-data recording expressions, and common column lookups
- centralize reusable utilities for both table and view code generation

### TableCrud

Responsibilities:

- generate CRUD members for regular tables
- own insert, insert-or-ignore, select-by-id, select-all, select-one, update, delete, and upsert generation

This layer depends on `Common`.

### TableQueryExtensions

Responsibilities:

- validate table query annotations
- generate `QueryBy`, `QueryLike`, and `QueryByOrCreate` members for tables

This layer depends on `Common`.

### TableGenerate

Responsibilities:

- orchestrate table validation and combine generated table members into the final type extension

This layer depends on the table CRUD and table query-extension layers.

### ViewQueries

Responsibilities:

- generate read-only query members for views
- validate view query annotations and generate custom view query methods

This layer depends on `Common`.

### ViewGenerate

Responsibilities:

- orchestrate validation and assembly of the final generated code for views

This is the top view-specific layer.

## Dependency Direction

The intended direction is:

`Common -> TableCrud/TableQueryExtensions/ViewQueries -> TableGenerate/ViewGenerate -> ../QueryGenerator.fs`

Lower layers should not depend on higher layers. Keep new code in the lowest layer that can own the responsibility cleanly.
