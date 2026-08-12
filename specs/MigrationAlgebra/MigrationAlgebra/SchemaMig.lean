/-
  Schema-level morphisms: the core of the migration algebra.

  Objects  = Schema
  Arrows   = SchemaMig S₀ S₁
  Identity = SchemaMig.id
  Compose  = SchemaMig.seq  (apply first morphism, then second)

  Phase 1: view catalog ops (create / drop / recreate).
  Phase 2: dependency-gated drop (and gated create/recreate):
    drop only with `NoDependentView`; create/recreate with Can* proofs.
    We do **not** encode “drop all views” as a primitive — only local gates.
-/

import MigrationAlgebra.Schema
import MigrationAlgebra.TableMig

namespace MigrationAlgebra

/-- Morphisms between schemas. -/
inductive SchemaMig : Schema → Schema → Type where
  /-- Empty migration. -/
  | id {s : Schema} : SchemaMig s s
  /-- Create a new base table. -/
  | createTable {s : Schema} (t : Table) : SchemaMig s (s.addTable t)
  /-- Drop a base table; requires no view still depends on its name. -/
  | dropTable {s : Schema} (n : String) (h : NoDependentView s n) :
      SchemaMig s (s.dropTable n)
  /-- Rename a base table. -/
  | renameTable {s : Schema} (fromName toName : String) :
      SchemaMig s (s.renameTable fromName toName)
  /-- Add a column on a base table. -/
  | addColumn {s : Schema} (tableName : String) (c : Column) :
      SchemaMig s (s.updateTable tableName (·.addColumn c))
  | dropColumn {s : Schema} (tableName : String) (colName : String) :
      SchemaMig s (s.updateTable tableName (·.dropColumn colName))
  | renameColumn {s : Schema} (tableName fromName toName : String) :
      SchemaMig s (s.updateTable tableName (·.renameColumn fromName toName))
  /-- Create a view; requires `CanCreateView` (fresh name, deps resolve). -/
  | createView {s : Schema} (v : View) (h : CanCreateView s v) :
      SchemaMig s (s.addView v)
  /-- Drop a view; requires no other view depends on its name. -/
  | dropView {s : Schema} (n : String) (h : NoDependentView s n) :
      SchemaMig s (s.dropView n)
  /-- Replace a view definition; requires `CanRecreateView`. -/
  | recreateView {s : Schema} (v : View) (h : CanRecreateView s v) :
      SchemaMig s (s.upsertView v)
  /-- Sequential composition: `seq m₂ m₁` means apply `m₁` then `m₂`. -/
  | seq {s₀ s₁ s₂ : Schema} :
      SchemaMig s₁ s₂ → SchemaMig s₀ s₁ → SchemaMig s₀ s₂

namespace SchemaMig

/-- Diagrammatic composition `m₂ ∘ m₁`. -/
def comp {s₀ s₁ s₂ : Schema} (m₂ : SchemaMig s₁ s₂) (m₁ : SchemaMig s₀ s₁) :
    SchemaMig s₀ s₂ :=
  .seq m₂ m₁

/-- Notation: `m₂ ∘ m₁` applies `m₁` first. -/
infixr:90 " ∘ₛ " => SchemaMig.comp

/-- Lift a pure table identity as a no-op schema migration (same schema). -/
def onTableId {s : Schema} (_tableName : String) : SchemaMig s s :=
  .id

/-- True when the morphism only touches the view catalog (syntactic class). -/
def isViewCatalog : {s₀ s₁ : Schema} → SchemaMig s₀ s₁ → Bool
  | _, _, .id => true
  | _, _, .createView _ _ => true
  | _, _, .dropView _ _ => true
  | _, _, .recreateView _ _ => true
  | _, _, .seq m₂ m₁ => isViewCatalog m₂ && isViewCatalog m₁
  | _, _, _ => false

end SchemaMig

/--
  A versioned path: ordered abstract migrations along a chain of schemas.
-/
inductive MigPath : Schema → Schema → Type where
  | nil {s : Schema} : MigPath s s
  | cons {s₀ s₁ s₂ : Schema} :
      SchemaMig s₀ s₁ → MigPath s₁ s₂ → MigPath s₀ s₂

namespace MigPath

def append {s₀ s₁ s₂ : Schema} :
    MigPath s₀ s₁ → MigPath s₁ s₂ → MigPath s₀ s₂
  | .nil, p => p
  | .cons m rest, p => .cons m (append rest p)

/-- Collapse a path to a single morphism via composition. -/
def flatten {s₀ s₁ : Schema} : MigPath s₀ s₁ → SchemaMig s₀ s₁
  | .nil => .id
  | .cons m rest => .seq (flatten rest) m

end MigPath

end MigrationAlgebra
