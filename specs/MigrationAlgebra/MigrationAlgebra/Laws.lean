/-
  Algebraic laws for schema migrations.

  Morphisms are an inductive free category with an explicit `seq` constructor,
  so *syntactic* associativity needs a setoid (or normal forms). What we prove
  here is the important *semantic* structure:

  * `applyMig` is a functor: preserves identity and composition.
-/

import MigrationAlgebra.SchemaMig
import MigrationAlgebra.Semantics

namespace MigrationAlgebra

/-- Identity is a semantic unit. -/
@[simp] theorem applyMig_id (s : Schema) (db : Instance) :
    applyMig (SchemaMig.id (s := s)) db = db :=
  rfl

/-- Composition is sequential application (apply `m₁` first). -/
@[simp] theorem applyMig_seq {s₀ s₁ s₂ : Schema}
    (m₂ : SchemaMig s₁ s₂) (m₁ : SchemaMig s₀ s₁) (db : Instance) :
    applyMig (.seq m₂ m₁) db = applyMig m₂ (applyMig m₁ db) :=
  rfl

/-- Semantic associativity of composition. -/
theorem applyMig_comp_assoc {s₀ s₁ s₂ s₃ : Schema}
    (m₃ : SchemaMig s₂ s₃) (m₂ : SchemaMig s₁ s₂) (m₁ : SchemaMig s₀ s₁)
    (db : Instance) :
    applyMig ((m₃ ∘ₛ m₂) ∘ₛ m₁) db = applyMig (m₃ ∘ₛ (m₂ ∘ₛ m₁)) db := by
  simp [SchemaMig.comp, applyMig]

/-- Left unit for `∘ₛ` under `applyMig`. -/
theorem applyMig_id_comp_left {s₀ s₁ : Schema}
    (m : SchemaMig s₀ s₁) (db : Instance) :
    applyMig (SchemaMig.id ∘ₛ m) db = applyMig m db := by
  simp [SchemaMig.comp, applyMig]

/-- Right unit for `∘ₛ` under `applyMig`. -/
theorem applyMig_comp_id_right {s₀ s₁ : Schema}
    (m : SchemaMig s₀ s₁) (db : Instance) :
    applyMig (m ∘ₛ SchemaMig.id) db = applyMig m db := by
  simp [SchemaMig.comp, applyMig]

/-- Empty path is identity. -/
theorem flatten_nil (s : Schema) :
    MigPath.flatten (.nil (s := s)) = SchemaMig.id :=
  rfl

/-- Singleton path is `id ∘ₛ m`, which is semantically `m`. -/
theorem flatten_cons_nil {s₀ s₁ : Schema} (m : SchemaMig s₀ s₁) :
    MigPath.flatten (.cons m .nil) = SchemaMig.id ∘ₛ m :=
  rfl

theorem applyMig_flatten_cons_nil {s₀ s₁ : Schema}
    (m : SchemaMig s₀ s₁) (db : Instance) :
    applyMig (MigPath.flatten (.cons m .nil)) db = applyMig m db := by
  simp [MigPath.flatten, applyMig]

/-!
  ## Worked example

  Create a table, then add a column. Dependent types track schema shape
  through composition.
-/

def users0 : Table :=
  { name := "users"
    cols := [{ name := "id", ty := .integer, nullable := false }] }

def emailCol : Column :=
  { name := "email", ty := .text, nullable := true }

def s0 : Schema := Schema.empty
def s1 : Schema := s0.addTable users0
def s2 : Schema := s1.updateTable "users" (·.addColumn emailCol)

def migCreate : SchemaMig s0 s1 := .createTable users0
def migAddEmail : SchemaMig s1 s2 := .addColumn "users" emailCol
def migBoth : SchemaMig s0 s2 := migAddEmail ∘ₛ migCreate

/-- Applying the composed migration yields the same instance as stepwise apply. -/
theorem migBoth_apply (db : Instance) :
    applyMig migBoth db = applyMig migAddEmail (applyMig migCreate db) := by
  simp [migBoth, SchemaMig.comp]

end MigrationAlgebra
