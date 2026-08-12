/-
  Schema-level morphisms: the core of the migration algebra.

  Objects  = Schema
  Arrows   = SchemaMig S₀ S₁
  Identity = SchemaMig.id
  Compose  = SchemaMig.seq  (apply first morphism, then second)

  Phase 1 view ops: createView / dropView / recreateView (catalog only).
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
  /-- Drop a base table by name. -/
  | dropTable {s : Schema} (n : String) : SchemaMig s (s.dropTable n)
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
  /-- Create a view in the catalog (no stored rows). -/
  | createView {s : Schema} (v : View) : SchemaMig s (s.addView v)
  /-- Drop a view by name. -/
  | dropView {s : Schema} (n : String) : SchemaMig s (s.dropView n)
  /-- Replace body/shape of a view (insert-or-replace by name). -/
  | recreateView {s : Schema} (v : View) : SchemaMig s (s.upsertView v)
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
  | _, _, .createView _ => true
  | _, _, .dropView _ => true
  | _, _, .recreateView _ => true
  | _, _, .seq m₂ m₁ => isViewCatalog m₂ && isViewCatalog m₁
  | _, _, _ => false

end SchemaMig

/--
  A versioned path: ordered abstract migrations along a chain of schemas.
  Concrete migrate SQL scripts are one *presentation* of such a path.
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
