/-
  Table-level morphisms.

  These capture independent single-table changes. Schema-level migrations may
  compose several table morphisms and also add/drop/rename whole tables.
-/

import MigrationAlgebra.Schema

namespace MigrationAlgebra

/-- Morphisms between tables (typed by source and target shape). -/
inductive TableMig : Table → Table → Type where
  /-- Identity: no change. -/
  | id {t : Table} : TableMig t t
  /-- Add a column that is not already present (precondition is proof-level). -/
  | addCol {t : Table} (c : Column) : TableMig t (t.addColumn c)
  /-- Drop a column by name. Target is `t` with that name filtered out. -/
  | dropCol {t : Table} (n : String) : TableMig t (t.dropColumn n)
  /-- Rename a column. -/
  | renCol {t : Table} (fromName toName : String) :
      TableMig t (t.renameColumn fromName toName)
  /-- Sequential composition: apply `m₁` first, then `m₂`. -/
  | seq {t₀ t₁ t₂ : Table} :
      TableMig t₁ t₂ → TableMig t₀ t₁ → TableMig t₀ t₂

namespace TableMig

/-- Compose two table migrations (diagrammatic order: `m₂ ∘ m₁`). -/
def comp {t₀ t₁ t₂ : Table} (m₂ : TableMig t₁ t₂) (m₁ : TableMig t₀ t₁) :
    TableMig t₀ t₂ :=
  .seq m₂ m₁

@[simp] def id_left {t₀ t₁ : Table} (m : TableMig t₀ t₁) : TableMig t₀ t₁ :=
  .seq (.id) m

@[simp] def id_right {t₀ t₁ : Table} (m : TableMig t₀ t₁) : TableMig t₀ t₁ :=
  .seq m (.id)

end TableMig

end MigrationAlgebra
