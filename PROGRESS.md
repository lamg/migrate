# Migration Progress: String Templates to Fabulous.AST v2

## Overview

Refactoring `QueryGenerator.fs` and `NormalizedQueryGenerator.fs` to use Fabulous.AST v2's DSL instead of string templates for generating F# code. This provides type-safe code generation with automatic Fantomas formatting.

## Recent Update

✅ Added `QueryLike(column)` annotation support to code generation:
- Parser support in FsLexYacc annotation extraction
- Table/view/normalized query generation for `LIKE '%' || @column || '%'`
- Validation that QueryLike uses exactly one existing column
- New test coverage for parsing, generation, and validation

## Key Accomplishments

✅ **29 methods successfully migrated** (9 sync table + 10 async table + 3 sync view + 3 async view + 3 normalized sync + 1 normalized async)
- All sync CRUD operations (Insert, Get, GetAll, GetOne, Update, Delete)
- All async CRUD operations (Insert, Get, GetAll, GetOne, Update, Delete)
- Custom query methods (QueryBy sync/async, QueryByOrCreate sync only)
- All view read operations (GetAll, GetOne, QueryBy - both sync and async)
- Normalized table sync methods (Insert, GetById, GetAll, GetOne, Update, Delete)
- Normalized Delete async method

✅ **Established consistent patterns** for AST-based code generation
- Parameter bindings with `ParenExpr` + `MatchExpr` + `AppExpr` for nullable handling
- Statement sequences using `ConstantExpr` and `trySqliteException`
- Helper functions (`pipeIgnore`, `returnOk`) for common patterns
- Hybrid approach for complex normalized tables: AST for simple cases, string templates for extension tables
- **New:** Async pattern using `taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]`

✅ **Test compatibility maintained**
- All 159 tests passing throughout migration
- Updated test expectations for multi-line match expression formatting
- Preserved functional behavior while improving code structure

## Status Summary

**Overall Progress:** 29 methods migrated (18 sync + 11 async)

| Phase | Component | Status | Notes |
|-------|-----------|--------|-------|
| 1 | AstExprBuilders.fs | ✅ Complete | Helper module with pipeIgnore, returnOk, taskExpr, trySqliteExceptionAsync |
| 2 | generateDelete (sync) | ✅ Complete | Migrated to AST |
| 2 | generateGetAll (sync) | ✅ Complete | Migrated to AST |
| 2 | generateGetOne (sync) | ✅ Complete | Migrated to AST |
| 2 | generateGet (sync) | ✅ Complete | Migrated to AST |
| 2 | generateUpdate (sync) | ✅ Complete | Migrated to AST with ParenExpr for match |
| 2 | generateInsert (sync) | ✅ Complete | Migrated to AST with match expressions |
| 2 | generateQueryBy (sync) | ✅ Complete | Migrated to AST with match expressions |
| 2 | generateQueryByOrCreate (sync) | ✅ Complete | Hybrid AST + ConstantExpr for complex logic |
| 3 | generateViewGetAll (sync) | ✅ Complete | Migrated to AST |
| 3 | generateViewGetOne (sync) | ✅ Complete | Migrated to AST |
| 3 | generateViewQueryBy (sync) | ✅ Complete | Migrated to AST with match expressions |
| 4 | Normalized generateDelete (sync) | ✅ Complete | Full AST migration |
| 4 | Normalized generateGetOne (sync) | ✅ Complete | Hybrid: AST for simple, string for extensions |
| 4 | Normalized generateGetById (sync) | ✅ Complete | Hybrid: AST for simple, string for extensions |
| 4 | Normalized generateGetAll (sync) | ✅ Complete | Hybrid: AST for simple, string for extensions |
| 4 | Normalized generateInsert (sync) | ✅ Complete | Hybrid: AST for simple, string for extensions |
| 4 | Normalized generateUpdate (sync) | ✅ Complete | Hybrid: AST for simple, string for extensions |
| 5 | generateDelete (async) | ✅ Complete | Migrated to AST with taskExpr |
| 5 | generateUpdate (async) | ✅ Complete | Migrated to AST with taskExpr |
| 5 | generateGetOne (async) | ✅ Complete | Migrated to AST with taskExpr |
| 5 | generateGet (async) | ✅ Complete | Migrated to AST with taskExpr |
| 5 | generateInsert (async) | ✅ Complete | Migrated to AST with taskExpr |
| 5 | generateGetAll (async) | ✅ Complete | Migrated to AST with while loop pattern |
| 5 | generateQueryBy (async) | ✅ Complete | Migrated to AST with while loop pattern |
| 5 | generateViewGetAll (async) | ✅ Complete | Migrated to AST with while loop pattern |
| 5 | generateViewGetOne (async) | ✅ Complete | Migrated to AST with taskExpr |
| 5 | generateViewQueryBy (async) | ✅ Complete | Migrated to AST with while loop pattern |
| 5 | Normalized generateDelete (async) | ✅ Complete | Migrated to AST with taskExpr |
| 5 | generateQueryByOrCreate (async) | ⏸️ Kept as string | Complex nested async calls |
| 5 | Normalized async (with extensions) | ⏸️ Kept as string | Complex match expressions + multi-table ops |

**Test Status:** All 135 tests passing

**Phases Complete:** 5/5 ✅

---

## Phase 1: Validation (Complete)

### Files Created/Modified

1. **Created:** `src/MigLib/CodeGen/AstExprBuilders.fs`
2. **Modified:** `src/MigLib/MigLib.fsproj` (added new file to compilation)
3. **Modified:** `src/MigLib/CodeGen/QueryGenerator.fs` (migrated generateDelete, generateGetAll)

### Key Implementation Patterns

#### Core Pattern

**Before (String Template):**
```fsharp
$"""  static member Delete {paramList} (tx: SqliteTransaction) : Result<unit, SqliteException> =
    try
      use cmd = new SqliteCommand("{deleteSql}", tx.Connection, tx)
      {paramBindings}
      cmd.ExecuteNonQuery() |> ignore
      Ok()
    with
    | :? SqliteException as ex -> Error ex"""
```

**After (Fabulous.AST):**
```fsharp
let bodyExprs =
  [ OtherExpr $"use cmd = new SqliteCommand(\"{deleteSql}\", tx.Connection, tx)" ]
  @ paramBindingStmts  // List of OtherExpr
  @ [ OtherExpr "cmd.ExecuteNonQuery() |> ignore"; OtherExpr "Ok()" ]

let memberName = $"Delete {paramList} (tx: SqliteTransaction)"
let returnType = "Result<unit, SqliteException>"
let body = trySqliteException bodyExprs

generateStaticMemberCode typeName memberName returnType body
```

#### Building Blocks

| Construct | AST Builder | Example |
|-----------|-------------|---------|
| Single statement | `OtherExpr "code"` | `OtherExpr "let x = 1"` |
| Multiple statements | `CompExprBodyExpr(seq)` | Combines OtherExpr list |
| Try/with SqliteException | `trySqliteException bodyExprs` | Wraps body in try/with |
| Static member | `Member(name, body, returnType).toStatic()` | Method declaration |
| Type augmentation | `Augmentation(typeName) { ... }` | `type X with` block |

#### Fantomas Configuration

```fsharp
let private formatConfig =
  { FormatConfig.Default with
      IndentSize = 2
      MaxLineLength = 200        // Prevents unwanted line wrapping
      SpaceBeforeMember = true } // Preserves "Method (param)" spacing
```

---

## Remaining Work

### Phase 2: QueryGenerator.fs Sync Methods

| Method | Complexity | Pattern | Status |
|--------|------------|---------|--------|
| `generateInsert` | Medium | Insert + last_insert_rowid | ✅ Complete |
| `generateGet` | Medium | Single row reader | ✅ Complete |
| `generateGetOne` | Low | Single row reader (LIMIT 1) | ✅ Complete |
| `generateUpdate` | Medium | Parameter bindings with match expr | ✅ Complete |
| `generateQueryBy` | Medium | Reader loop with WHERE | ✅ Complete |
| `generateQueryByOrCreate` | High | Conditional insert/select | ✅ Complete |

### Phase 3: QueryGenerator.fs View Methods

| Method | Complexity | Pattern | Status |
|--------|------------|---------|--------|
| `generateViewGetAll` | Low | Reader loop | ✅ Complete |
| `generateViewGetOne` | Low | Single row reader | ✅ Complete |
| `generateViewQueryBy` | Medium | Reader loop with WHERE | ✅ Complete |

### Phase 4: NormalizedQueryGenerator.fs Sync Methods (Complete)

| Method | Complexity | Pattern | Status |
|--------|------------|---------|--------|
| `generateDelete` | Medium | Simple DELETE with PK params | ✅ Complete (Full AST) |
| `generateGetOne` | Medium | Case selection with extensions | ✅ Complete (Hybrid) |
| `generateGetById` | Medium | Case selection with PK params | ✅ Complete (Hybrid) |
| `generateGetAll` | Medium | While loop with case selection | ✅ Complete (Hybrid) |
| `generateInsert` | High | Multi-table insert with match | ✅ Complete (Hybrid) |
| `generateUpdate` | High | Multi-table update with match | ✅ Complete (Hybrid) |

**Approach Used:** Hybrid strategy based on complexity:
- **Simple case (no extensions):** Full AST migration using `trySqliteException`, `ConstantExpr`, `generateStaticMemberCode`
- **Complex case (with extensions):** String template for body due to multi-line match expressions and `generateCaseSelection` logic

### Phase 5: Async Methods (Complete)

**Status:** ✅ 11 async methods migrated, 2 kept as string templates (complex cases)

Async methods use `task { }` computation expressions. Successfully migrated using the pattern:

```fsharp
let asyncBodyExprs = [ OtherExpr "..." ; ... ]
let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]
generateStaticMemberCode typeName memberName returnType body
```

**Migrated Methods (QueryGenerator.fs):**
- `generateDelete` (async) - Simple ExecuteNonQueryAsync
- `generateUpdate` (async) - ExecuteNonQueryAsync with parameter bindings
- `generateGetOne` (async) - Reader with if/else
- `generateGet/GetById` (async) - Reader with parameter bindings and if/else
- `generateInsert` (async) - ExecuteNonQueryAsync + last_insert_rowid
- `generateGetAll` (async) - While loop for async reading
- `generateQueryBy` (async) - While loop with parameter bindings
- `generateViewGetAll` (async) - While loop for views
- `generateViewGetOne` (async) - Reader with if/else for views
- `generateViewQueryBy` (async) - While loop for views

**Migrated Methods (NormalizedQueryGenerator.fs):**
- `generateDelete` (async) - Simple ExecuteNonQueryAsync

**Kept as String Templates (Category C - Complex):**
- `generateQueryByOrCreate` (async) - Nested async calls with Insert + GetById
- Normalized table methods with extensions (Insert, Update, GetAll, GetById, GetOne async) - Complex match expressions and multi-table operations

**Key Pattern for While Loops:**
```fsharp
let whileLoopBody =
  $"let mutable hasMore = true in while hasMore do let! next = reader.ReadAsync() in hasMore <- next; if hasMore then results.Add({{ {fieldMappings} }})"
```

---

## Migration Guide for Each Method

### Step 1: Identify Body Structure

Look at the method body and identify:
- Setup statements (use cmd, use reader, let results)
- Core logic (while loops, if statements)
- Return statement (Ok(...) or Error)

### Step 2: Build Statement List

```fsharp
let bodyExprs = [
  OtherExpr "use cmd = new SqliteCommand(...)"
  OtherExpr "use reader = cmd.ExecuteReader()"
  OtherExpr "let results = ResizeArray<T>()"
  OtherExpr "while reader.Read() do results.Add({ ... })"
  OtherExpr "Ok(results |> Seq.toList)"
]
```

### Step 3: Wrap in Try/With

```fsharp
let body = trySqliteException bodyExprs
```

### Step 4: Generate Member Code

```fsharp
let memberName = $"MethodName {params} (tx: SqliteTransaction)"
let returnType = "Result<T, SqliteException>"
generateStaticMemberCode typeName memberName returnType body
```

### Step 5: Test

Run `dotnet test` to verify output matches expected format.

---

## Key Learnings

### AST vs String Templates

**When AST builders work well:**
- Simple parameter bindings with `AppExpr` and `pipeIgnore`
- Match expressions for nullable handling with `ParenExpr` + `MatchExpr`
- Sequential statements with `ConstantExpr` lists
- Try/with blocks with `trySqliteException`

**When to use ConstantExpr with strings:**
- Complex control flow (nested if/then/else with matches)
- Very long single-line expressions that Fantomas would reformat anyway
- When the AST structure becomes more complex than the code it represents

**Hybrid approach:**
For complex methods like `generateQueryByOrCreate`, use AST builders for repetitive patterns (parameter bindings) and `ConstantExpr` for complex control flow.

### Formatting Changes

**Multi-line match expressions:**
Fabulous.AST + Fantomas formats match expressions across multiple lines, even when originally single-line. Tests need to check for component parts rather than exact strings.

**Before (string template):**
```fsharp
"match email with Some v -> box v | None -> box DBNull.Value"
```

**After (AST generated):**
```fsharp
match email with
| Some v -> box v
| None -> box DBNull.Value
```

**Test updates required:**
```fsharp
// Old: Assert.Contains("match email with Some v -> box v | None -> box DBNull.Value", code)
// New:
Assert.Contains("match email with", code)
Assert.Contains("Some v -> box v", code)
Assert.Contains("None -> box DBNull.Value", code)
```

### Comments Not Preserved

AST-generated code doesn't preserve comments from string templates. Tests checking for specific comments (e.g., "Not found - insert and fetch") need to be updated to check for functional code elements instead.

## Common Pitfalls

1. **Type annotations** - `OtherExpr` returns `WidgetBuilder<ComputationExpressionStatement>`, not `WidgetBuilder<Expr>`

2. **Method name prefix** - Use `"Delete ..."` not `"_.Delete ..."` for standard static methods

3. **Fantomas line wrapping** - Long SQL strings may wrap; `MaxLineLength = 200` prevents this

4. **Spacing normalization** - Fantomas may change spacing; `SpaceBeforeMember = true` preserves `Method (param)` format

5. **Record literals in loops** - Fantomas expands `{ A = x; B = y }` to multi-line format; this is acceptable

6. **Field mapping separators** - Use semicolons (`;`) for inline record literals, not newlines (`\n`)

---

## Phase 4 Complete: Normalized Schema Methods

Phase 4 successfully migrated all 6 sync methods in `NormalizedQueryGenerator.fs` that handle normalized schemas (2NF) with discriminated unions.

**Approach:** Hybrid strategy based on complexity:

| Scenario | Approach | Rationale |
|----------|----------|-----------|
| No extensions | Full AST | Simple base record, straightforward logic |
| With extensions | String template for body | Complex `generateCaseSelection` with NULL checks, multi-line match patterns |

**Key Implementation Details:**

1. **generateDelete** - Full AST migration (simplest method, no DU matching needed)
2. **generateGetOne/GetById/GetAll** - Hybrid: AST for no-extension case, string template when extensions require `generateCaseSelection`
3. **generateInsert/Update** - Hybrid: AST for base-only DU case with embedded match expression, string template for multi-table operations with extensions

**What Stays as String Templates:**
- `generateBaseCase` and `generateExtensionCase` helpers (produce match arms)
- `generateCaseSelection` helper (NULL checks + match patterns for DU construction)
- Complex control flow in extension table operations

---

## What Stays as Strings

- **SQL queries** - Embedded in OtherExpr/ConstantExpr as interpolated strings
- **ProjectGenerator.fs** - Generates XML, not F#
- **Complex control flow** - Nested if/then/else with multiple match expressions
- **Normalized table extension cases** - `generateCaseSelection`, `generateBaseCase`, `generateExtensionCase` helpers produce complex multi-line match arms that don't fit cleanly into AST builders
- **generateQueryByOrCreate async** - Complex nested async calls (Insert, then GetById)
- **Normalized async methods with extensions** - Complex match expressions + multi-table operations

---

## File Structure

```
src/MigLib/CodeGen/
├── FabulousAstHelpers.fs    # Low-level Oak AST helpers
├── AstExprBuilders.fs       # High-level query generation helpers
├── ViewIntrospection.fs
├── TypeGenerator.fs         # Already uses Fabulous.AST
├── NormalizedSchema.fs
├── NormalizedTypeGenerator.fs # Already uses Fabulous.AST
├── NormalizedQueryGenerator.fs # Sync methods migrated (hybrid approach)
├── QueryGenerator.fs        # Sync methods migrated
├── FileMapper.fs
├── ProjectGenerator.fs      # Keep as strings (XML)
└── CodeGen.fs
```

---

## Verification Checklist

Before marking a method as complete:

- [x] `dotnet build` succeeds
- [x] `dotnet test` passes (all 135 tests)
- [x] `dotnet fantomas .` produces no changes
- [x] Generated code compiles in target projects
- [x] Output format matches original (modulo acceptable Fantomas differences)

**Phase 4 Verification:** All checks passed on 2026-02-01
**Phase 5 Verification:** All checks passed on 2026-02-01

---

## Phase 5 Complete: Async Methods Migration

Phase 5 successfully migrated 11 async methods to use Fabulous.AST with the `taskExpr` and `trySqliteExceptionAsync` helpers.

**Key Implementation Pattern:**

```fsharp
// For simple async methods (Delete, Update, Insert, GetOne, GetById)
let asyncBodyExprs =
  [ OtherExpr $"use cmd = new SqliteCommand(\"{sql}\", tx.Connection, tx)" ]
  @ paramBindingExprs
  @ [ OtherExpr "let! _ = cmd.ExecuteNonQueryAsync()"; OtherExpr "return Ok()" ]

let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]
generateStaticMemberCode typeName memberName returnType body

// For while loop methods (GetAll, QueryBy)
let whileLoopBody =
  $"let mutable hasMore = true in while hasMore do let! next = reader.ReadAsync() in hasMore <- next; if hasMore then results.Add({{ {fieldMappings} }})"
```

**Understanding the Pattern:**
1. `OtherExpr` converts a string to `WidgetBuilder<ComputationExpressionStatement>`
2. `trySqliteExceptionAsync` wraps statements in `try/with` and returns `WidgetBuilder<Expr>`
3. `OtherExpr(trySqliteExceptionAsync ...)` converts the Expr back to ComputationExpressionStatement
4. `taskExpr` wraps everything in `task { }` computation expression

**What Stays as String Templates:**
- `generateQueryByOrCreate` async - Complex nested async calls with Insert + conditional GetById
- Normalized async methods with extensions - Complex `generateCaseSelection` + multi-table operations
