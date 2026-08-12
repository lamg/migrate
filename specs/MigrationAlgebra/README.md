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
| `MigrationAlgebra.Schema` | `SqlType`, `Column`, `Table`, `Schema` |
| `MigrationAlgebra.TableMig` | Table-level morphisms |
| `MigrationAlgebra.SchemaMig` | Schema-level morphisms + composition |
| `MigrationAlgebra.Semantics` | Instances and `apply` |
| `MigrationAlgebra.Laws` | Identity / composition properties |

## Design sketch

```text
Schemas  = objects
Mig S₀ S₁ = morphisms  S₀ ⟶ S₁
apply     : Mig S₀ S₁ → Instance S₀ → Instance S₁

id        : Mig S S
(∘)       : Mig S₁ S₂ → Mig S₀ S₁ → Mig S₀ S₂
```

Ordered SQL scripts (as in migrate) are a *presentation* of such morphisms;
many script lists may denote the same abstract migration.
