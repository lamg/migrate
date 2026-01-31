# Migration Progress: String Templates to Fabulous.AST v2

## Overview

Refactoring `QueryGenerator.fs` and `NormalizedQueryGenerator.fs` to use Fabulous.AST v2's DSL instead of string templates for generating F# code. This provides type-safe code generation with automatic Fantomas formatting.

## Key Accomplishments

✅ **12 methods successfully migrated** (9 table methods + 3 view methods)
- All sync CRUD operations (Insert, Get, GetAll, GetOne, Update, Delete)
- Custom query methods (QueryBy, QueryByOrCreate)
- All view read operations (GetAll, GetOne, QueryBy)

✅ **Established consistent patterns** for AST-based code generation
- Parameter bindings with `ParenExpr` + `MatchExpr` + `AppExpr` for nullable handling
- Statement sequences using `ConstantExpr` and `trySqliteException`
- Helper functions (`pipeIgnore`, `returnOk`) for common patterns

✅ **Test compatibility maintained**
- All 135 tests passing throughout migration
- Updated test expectations for multi-line match expression formatting
- Preserved functional behavior while improving code structure

## Status Summary

**Overall Progress:** 9/14 sync methods migrated (64%)

| Phase | Component | Status | Notes |
|-------|-----------|--------|-------|
| 1 | AstExprBuilders.fs | ✅ Complete | Helper module with pipeIgnore, returnOk |
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
| 4 | Normalized methods (5) | 🔲 Pending | High complexity multi-table operations |
| 5 | Async methods | ⏸️ Deferred | Task CE is complex; keeping string templates |

**Test Status:** All 135 tests passing

**Phases Complete:** 3/5 (Phases 1-3 ✅, Phase 4 pending, Phase 5 deferred)

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

### Phase 4: NormalizedQueryGenerator.fs Sync Methods

| Method | Complexity | Pattern | Status |
|--------|------------|---------|--------|
| `generateInsert` | High | Multi-table insert with match | 🔲 Pending |
| `generateGetById` | High | Multi-table query with match | 🔲 Pending |
| `generateGetAll` | High | Multi-table reader loop | 🔲 Pending |
| `generateUpdate` | High | Multi-table update with match | 🔲 Pending |
| `generateDelete` | Medium | Cascading delete | 🔲 Pending |

### Phase 5: Async Methods (Optional)

Async methods use `task { }` computation expressions with `try/with` inside, which is complex to express in Fabulous.AST. Options:

1. **Keep as string templates** - Current approach, works well
2. **Hybrid approach** - Use AST for structure, embed body as string
3. **Full AST** - Requires building nested computation expressions

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

## Next Steps: Phase 4 - Normalized Schema Methods

Phase 4 involves migrating methods in `NormalizedQueryGenerator.fs` that handle normalized schemas (2NF) with discriminated unions. These methods are significantly more complex:

**Challenges:**
- Multi-table operations (insert/query across linked tables)
- Discriminated union construction from query results
- Complex match expressions for union case handling
- Cascading operations (e.g., delete with dependent tables)

**Methods to migrate:**
1. `generateInsert` - Multi-table insert with union construction
2. `generateGetById` - Multi-table join with union matching
3. `generateGetAll` - Multi-table reader loop with union construction
4. `generateUpdate` - Multi-table update with union deconstruction
5. `generateDelete` - Cascading delete across dependent tables

**Recommendation:** These methods may benefit from continuing the hybrid approach (AST for repetitive patterns, ConstantExpr for complex logic) established in `generateQueryByOrCreate`.

---

## What Stays as Strings

- **SQL queries** - Embedded in ConstantExpr as interpolated strings
- **Async methods** - Task CE with try/with is complex; deferred to Phase 5
- **ProjectGenerator.fs** - Generates XML, not F#
- **Complex control flow** - Nested if/then/else with multiple match expressions

---

## File Structure

```
src/MigLib/CodeGen/
├── FabulousAstHelpers.fs    # Low-level Oak AST helpers
├── AstExprBuilders.fs       # High-level query generation helpers (NEW)
├── ViewIntrospection.fs
├── TypeGenerator.fs         # Already uses Fabulous.AST
├── NormalizedSchema.fs
├── NormalizedTypeGenerator.fs # Already uses Fabulous.AST
├── NormalizedQueryGenerator.fs # Needs migration
├── QueryGenerator.fs        # Partially migrated
├── FileMapper.fs
├── ProjectGenerator.fs      # Keep as strings (XML)
└── CodeGen.fs
```

---

## Verification Checklist

Before marking a method as complete:

- [ ] `dotnet build` succeeds
- [ ] `dotnet test` passes (all 135 tests)
- [ ] `dotnet fantomas .` produces no changes
- [ ] Generated code compiles in target projects
- [ ] Output format matches original (modulo acceptable Fantomas differences)
