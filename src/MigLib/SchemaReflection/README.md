# SchemaReflection Layers

This directory contains the internal layers behind the public `Mig.SchemaReflection` facade.

`../SchemaReflection.fs` stays intentionally small and re-exports the stable entry points. The implementation is split here by responsibility so lower-level reflection helpers do not depend on higher-level schema orchestration.

## Layers

### Types

Responsibilities:

- define small internal helper types shared across schema reflection
- keep cross-layer data contracts such as primary key metadata and inferred view joins in one place

### NamingAndTypeMapping

Responsibilities:

- convert between CLR naming conventions and SQL naming conventions
- detect supported record and union shapes
- map CLR scalar types into declarative migration SQL types
- parse default literals used by reflected attributes

This is a low-level helper layer with no knowledge of whole-schema workflows.

### AttributeReaders

Responsibilities:

- read and validate custom attributes from reflected record types
- resolve field and column names consistently
- construct constraints, index definitions, and query annotations from attribute data

This layer depends on `NamingAndTypeMapping` and provides reusable metadata extraction.

### TableReflection

Responsibilities:

- turn record types into `CreateTable` definitions
- apply column and table constraints
- wire foreign keys to other reflected schema types

This layer depends on lower naming and attribute-reading layers.

### ViewReflection

Responsibilities:

- infer join structures for reflected views
- synthesize SQL for `[<View>]` definitions or validate `[<ViewSql>]`
- resolve projected view fields against joined tables

This layer depends on the lower naming and attribute-reading layers.

### UnionExtensions

Responsibilities:

- convert supported union cases into extension tables tied to a base record table
- reuse reflected primary key information to build those extension tables safely

This layer depends on naming/type mapping plus primary key metadata.

### SchemaAssembly

Responsibilities:

- orchestrate full schema construction from a set of reflected types or an assembly
- combine tables, indexes, extension tables, and views into one `SqlFile`
- validate cross-table invariants such as unique table names

This is the main assembly-level orchestration layer.

### SeedReflection

Responsibilities:

- discover static seed values from compiled schema modules
- convert record values into seed insert expressions
- merge those inserts into the reflected schema produced by `SchemaAssembly`

This is the top layer in this area because it composes the full reflected schema with seed extraction.

## Dependency Direction

The intended direction is:

`Types -> NamingAndTypeMapping -> AttributeReaders -> TableReflection/ViewReflection/UnionExtensions -> SchemaAssembly -> SeedReflection -> ../SchemaReflection.fs`

Lower layers should not depend on higher ones. Keep new code in the lowest layer that can own the responsibility cleanly.
