# MigrationAlgebra

Lean 4 exploration of an **algebra of database migrations**: schemas as objects,
migrations as morphisms, application as a semantics on database instances.

This package states **algebraic contracts** a future MigLib-style runner can
follow. It is not a runtime, not codegen, and not a full schema engine.

## Build

Requires [elan](https://github.com/leanprover/elan) and the toolchain pinned in
`lean-toolchain` (Lean 4.33).

```bash
cd specs/MigrationAlgebra
lake build
```

## Modules

| Module | Role |
|--------|------|
| `MigrationAlgebra.Schema` | `SqlType`, `Column`, `Table`, `View`, `Schema`, gates |
| `MigrationAlgebra.TableMig` | Table-level morphisms |
| `MigrationAlgebra.SchemaMig` | Schema-level morphisms + `MigPath` |
| `MigrationAlgebra.Semantics` | Instances and `applyMig` |
| `MigrationAlgebra.Coupling` | Dependency preservation under gated drops/creates |
| `MigrationAlgebra.WellFormed` | Full `WellFormed` for `dropTable` / `dropColumn` |
| `MigrationAlgebra.Laws` | Functoriality, examples |
| `MigrationAlgebra.Policy` | Phase 3 law pack / admissibility |

## Design sketch

```text
Schemas  = objects (tables + views)
Mig S₀ S₁ = morphisms  S₀ ⟶ S₁
apply     : Mig S₀ S₁ → Instance → Instance

id        : Mig S S
(∘)       : Mig S₁ S₂ → Mig S₀ S₁ → Mig S₀ S₂
```

### Phase 1 — Vocabulary

Views are catalog objects with `name`, `cols`, `deps` (name-level). Morphisms
`createView` / `dropView` / `recreateView`. Pure view ops are identity on
stored rows. Well-formedness: shared name space, resolved deps, topo-ordered
view list.

### Phase 2 — Dependency gates

Destructive drops are **dependency-gated**, not a global “drop every view”
primitive:

- `NoDependentView s n` — no view lists `n` in `deps`
- `dropView` / `dropTable` require that proof
- `dropColumn` / `renameTable` also require a name-level gate
  (`NoDependentView` on the table / `CanRenameTable`)
- `CanCreateView` / `CanRecreateView` gate create/recreate
- Multi-view teardown is a **path** of local gates

### Phase 3 — Policy (laws for implementors)

Module `Policy` packages the contracts:

1. **Functoriality** of `applyMig` (see also `Laws`)
2. **`DataPreserving`** — view-catalog morphisms do not change instances  
   (`DataPreserving_of_isViewCatalog`)
3. **Gated drops/creates** — constructors carry `NoDependentView` / `Can*`
4. **`PreservesResolvedDeps`** — gated create/drop keep view deps resolved
5. **`PreservesWellFormed`** — `dropTable` / `dropColumn` keep full `WellFormed`
6. **`DepSafePath`** — path-level policy on resolved deps along `MigPath`
6. **Catalog vs data** — `conforms` is about tables; view steps leave rows alone

Out of scope here: query bodies, column-level reads, codegen annotations.
