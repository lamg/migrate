# Migration Progress: String Templates to Fabulous.AST v2

## Overview

Refactoring `QueryGenerator.fs` and `NormalizedQueryGenerator.fs` to use Fabulous.AST v2's DSL instead of string templates for generating F# code. This provides type-safe code generation with automatic Fantomas formatting.

## Status Summary

| Component | Status | Notes |
|-----------|--------|-------|
| AstExprBuilders.fs | ✅ Complete | Helper module created |
| generateDelete (sync) | ✅ Complete | Migrated to AST |
| generateGetAll (sync) | ✅ Complete | Migrated to AST |
| generateGetOne (sync) | ✅ Complete | Migrated to AST |
| generateGet (sync) | ✅ Complete | Migrated to AST |
| Async methods | ⏸️ Deferred | Task CE is complex; keeping string templates |
| Remaining sync methods | 🔲 Pending | See list below |

**Test Status:** All 135 tests passing

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
| `generateInsert` | Medium | Insert + last_insert_rowid | 🔲 Pending |
| `generateGet` | Medium | Single row reader | ✅ Complete |
| `generateGetOne` | Low | Single row reader (LIMIT 1) | ✅ Complete |
| `generateUpdate` | Medium | Parameter bindings | 🔲 Pending |
| `generateQueryBy` | Medium | Reader loop with WHERE | 🔲 Pending |
| `generateQueryByOrCreate` | High | Conditional insert/select | 🔲 Pending |

### Phase 3: QueryGenerator.fs View Methods

| Method | Complexity | Pattern | Status |
|--------|------------|---------|--------|
| `generateViewGetAll` | Low | Reader loop | 🔲 Pending |
| `generateViewGetOne` | Low | Single row reader | 🔲 Pending |
| `generateViewQueryBy` | Medium | Reader loop with WHERE | 🔲 Pending |

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

## Common Pitfalls

1. **Type annotations** - `OtherExpr` returns `WidgetBuilder<ComputationExpressionStatement>`, not `WidgetBuilder<Expr>`

2. **Method name prefix** - Use `"Delete ..."` not `"_.Delete ..."` for standard static methods

3. **Fantomas line wrapping** - Long SQL strings may wrap; `MaxLineLength = 200` prevents this

4. **Spacing normalization** - Fantomas may change spacing; `SpaceBeforeMember = true` preserves `Method (param)` format

5. **Record literals in loops** - Fantomas expands `{ A = x; B = y }` to multi-line format; this is acceptable

---

## What Stays as Strings

- **SQL queries** - Embedded in OtherExpr as interpolated strings
- **Async methods** - Task CE with try/with is complex
- **ProjectGenerator.fs** - Generates XML, not F#

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
