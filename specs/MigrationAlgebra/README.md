# MigrationAlgebra

Lean 4 exploration of an **algebra of database migrations**: schemas as objects,
migrations as morphisms, application as a semantics on database instances.

This is a design / formalization workspace under `specs/`. It is **not** wired
into the F# `migrate` runtime; the goal is to make the algebra precise before
(or while) shaping tooling.

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
| `MigrationAlgebra.Schema` | `SqlType`, `Column`, `Table`, `View`, `Schema` |
| `MigrationAlgebra.TableMig` | Table-level morphisms |
| `MigrationAlgebra.SchemaMig` | Schema-level morphisms + composition |
| `MigrationAlgebra.Semantics` | Instances and `applyMig` |
| `MigrationAlgebra.Coupling` | Phase 2: dep-gated drops preserve resolved deps |
| `MigrationAlgebra.Laws` | Identity / composition / view data-preservation / examples |

## Design sketch

```text
Schemas  = objects (tables + views)
Mig S₀ S₁ = morphisms  S₀ ⟶ S₁
apply     : Mig S₀ S₁ → Instance → Instance

id        : Mig S S
(∘)       : Mig S₁ S₂ → Mig S₀ S₁ → Mig S₀ S₂
```

**Views (Phase 1):** catalog objects with `name`, `cols`, `deps`. Morphisms
`createView` / `dropView` / `recreateView`. `applyMig` is **identity on stored
rows** for pure view ops. Well-formedness: shared name space, resolved deps,
topo-ordered view list (acyclicity witness).

**Coupling (Phase 2):** destructive drops are **dependency-gated**, not
“drop all views”:

- `NoDependentView s n` — no view lists `n` in `deps`
- `dropView` / `dropTable` require that proof
- `CanCreateView` / `CanRecreateView` gate create/recreate
- SQL “drop many views then alter” is only one *path* that discharges gates

Ordered SQL scripts (as in migrate) are a *presentation* of such morphisms;
many script lists may denote the same abstract migration.

Codegen annotations (`select_with`, etc.) stay outside this algebra.
