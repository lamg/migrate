/-
  Algebraic laws for schema migrations.

  Morphisms are an inductive free category with an explicit `seq` constructor,
  so *syntactic* associativity needs a setoid (or normal forms). What we prove
  here is the important *semantic* structure:

  * `applyMig` is a functor: preserves identity and composition.
  * view catalog ops preserve stored data
  * Phase 2: dependency-gated drops (examples)
-/

import MigrationAlgebra.SchemaMig
import MigrationAlgebra.Semantics
import MigrationAlgebra.Coupling

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

/-! ### View catalog ops preserve stored data -/

@[simp] theorem applyMig_createView {s : Schema} (v : View)
    (h : CanCreateView s v) (db : Instance) :
    applyMig (SchemaMig.createView (s := s) v h) db = db :=
  rfl

@[simp] theorem applyMig_dropView {s : Schema} (n : String)
    (h : NoDependentView s n) (db : Instance) :
    applyMig (SchemaMig.dropView (s := s) n h) db = db :=
  rfl

@[simp] theorem applyMig_recreateView {s : Schema} (v : View)
    (h : CanRecreateView s v) (db : Instance) :
    applyMig (SchemaMig.recreateView (s := s) v h) db = db :=
  rfl

/-!
  ## Worked examples (Phase 1 + Phase 2)
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

def activeUsers : View :=
  { name := "active_users"
    cols := [{ name := "id", ty := .integer, nullable := false }]
    deps := ["users"] }

theorem canCreate_activeUsers : CanCreateView s2 activeUsers := by
  refine ⟨?_, ?_, ?_⟩
  · -- fresh name
    decide
  · -- deps resolve
    intro d hd
    have : d = "users" := by
      simp [activeUsers] at hd
      exact hd
    subst this
    decide
  · -- col names nodup
    decide

def s3 : Schema := s2.addView activeUsers
def migCreateView : SchemaMig s2 s3 := .createView activeUsers canCreate_activeUsers
def migTableThenView : SchemaMig s0 s3 := migCreateView ∘ₛ migBoth

/-- View creation does not change the stored instance produced by table migs. -/
theorem migTableThenView_data (db : Instance) :
    applyMig migTableThenView db = applyMig migBoth db := by
  simp [migTableThenView, migCreateView, SchemaMig.comp, applyMig]

def activeUsers' : View where
  name := "active_users"
  cols :=
    [ { name := "id", ty := .integer, nullable := false }
      , { name := "email", ty := .text, nullable := true } ]
  deps := ["users"]

theorem canRecreate_activeUsers' : CanRecreateView s3 activeUsers' := by
  refine ⟨?_, ?_, ?_⟩
  · decide
  · intro d hd
    have : d = "users" := by
      simp [activeUsers'] at hd
      exact hd
    subst this
    decide
  · decide

def s4 : Schema := s3.upsertView activeUsers'
def migRecreate : SchemaMig s3 s4 := .recreateView activeUsers' canRecreate_activeUsers'

theorem recreateView_preserves (db : Instance) :
    applyMig migRecreate db = db := by
  simp [migRecreate, applyMig]

/-!
  ### Phase 2 path: drop dependent view, then drop table

  Algebraic style is local gates (`NoDependentView`), not “drop every view”.
  Order: drop `active_users` (nothing depends on it) → drop `users`.
-/

theorem noDep_on_activeUsers : NoDependentView s3 "active_users" := by
  decide

/-- After the view is gone, nothing depends on `users`. -/
def s3_noView : Schema := s3.dropView "active_users"

theorem noDep_on_users_after_view_drop : NoDependentView s3_noView "users" := by
  decide

def migDropActive : SchemaMig s3 s3_noView :=
  .dropView "active_users" noDep_on_activeUsers

def s_emptyish : Schema := s3_noView.dropTable "users"

def migDropUsers : SchemaMig s3_noView s_emptyish :=
  .dropTable "users" noDep_on_users_after_view_drop

/-- Composite: tear down view then table (proof-carrying at each step). -/
def migTeardown : SchemaMig s3 s_emptyish :=
  migDropUsers ∘ₛ migDropActive

theorem migTeardown_apply (db : Instance) :
    applyMig migTeardown db = db.erase "users" := by
  simp [migTeardown, migDropUsers, migDropActive, SchemaMig.comp, applyMig, Instance.erase]

/-- Nested view: must drop the leaf before the middle view. -/
def vipUsers : View :=
  { name := "vip_users"
    cols := [{ name := "id", ty := .integer, nullable := false }]
    deps := ["active_users"] }

theorem canCreate_vip : CanCreateView s3 vipUsers := by
  refine ⟨?_, ?_, ?_⟩
  · decide
  · intro d hd
    have : d = "active_users" := by
      simp [vipUsers] at hd
      exact hd
    subst this
    decide
  · decide

def s3vip : Schema := s3.addView vipUsers

/-- Cannot drop `active_users` while `vip_users` depends on it — gate fails. -/
example : noDependentViewb s3vip "active_users" = false := by decide

theorem noDep_vip : NoDependentView s3vip "vip_users" := by decide

def s3vip_mid : Schema := s3vip.dropView "vip_users"
def migDropVip : SchemaMig s3vip s3vip_mid := .dropView "vip_users" noDep_vip

theorem noDep_active_after_vip : NoDependentView s3vip_mid "active_users" := by decide

def s3vip_base : Schema := s3vip_mid.dropView "active_users"
def migDropActiveAfterVip : SchemaMig s3vip_mid s3vip_base :=
  .dropView "active_users" noDep_active_after_vip

end MigrationAlgebra
