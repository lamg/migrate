/-
  Phase 3: policy / law pack.

  Contracts a future migration implementation is expected to follow.
  No query bodies — only schema morphisms, dependency gates, and
  applyMig laws already established in the algebra.
-/

import MigrationAlgebra.Schema
import MigrationAlgebra.SchemaMig
import MigrationAlgebra.Semantics
import MigrationAlgebra.Coupling

namespace MigrationAlgebra

/-! ## Data preservation

View-catalog morphisms must not change stored table rows.
`DataPreserving` packages that contract.
-/

/-- `m` does not change any database instance. -/
def DataPreserving {s₀ s₁ : Schema} (m : SchemaMig s₀ s₁) : Prop :=
  ∀ db : Instance, applyMig m db = db

@[simp] theorem DataPreserving_id (s : Schema) :
    DataPreserving (SchemaMig.id (s := s)) := by
  intro db; rfl

@[simp] theorem DataPreserving_createView {s : Schema} (v : View)
    (h : CanCreateView s v) :
    DataPreserving (SchemaMig.createView (s := s) v h) := by
  intro db; rfl

@[simp] theorem DataPreserving_dropView {s : Schema} (n : String)
    (h : NoDependentView s n) :
    DataPreserving (SchemaMig.dropView (s := s) n h) := by
  intro db; rfl

@[simp] theorem DataPreserving_recreateView {s : Schema} (v : View)
    (h : CanRecreateView s v) :
    DataPreserving (SchemaMig.recreateView (s := s) v h) := by
  intro db; rfl

theorem DataPreserving_seq {s₀ s₁ s₂ : Schema}
    (m₂ : SchemaMig s₁ s₂) (m₁ : SchemaMig s₀ s₁)
    (h₂ : DataPreserving m₂) (h₁ : DataPreserving m₁) :
    DataPreserving (.seq m₂ m₁) := by
  intro db
  simp [applyMig, h₁ db, h₂ db]

/-- Composition of data-preserving morphisms (diagrammatic order). -/
theorem DataPreserving_comp {s₀ s₁ s₂ : Schema}
    (m₂ : SchemaMig s₁ s₂) (m₁ : SchemaMig s₀ s₁)
    (h₂ : DataPreserving m₂) (h₁ : DataPreserving m₁) :
    DataPreserving (m₂ ∘ₛ m₁) := by
  simpa [SchemaMig.comp] using DataPreserving_seq m₂ m₁ h₂ h₁

/--
  Every view-catalog morphism (by `isViewCatalog`) is data-preserving.
  This is the main Phase 3 data law for the catalog fragment.
-/
theorem DataPreserving_of_isViewCatalog {s₀ s₁ : Schema}
    (m : SchemaMig s₀ s₁) (h : SchemaMig.isViewCatalog m = true) :
    DataPreserving m := by
  induction m with
  | id => exact DataPreserving_id _
  | createView v hv => exact DataPreserving_createView v hv
  | dropView n hn => exact DataPreserving_dropView n hn
  | recreateView v hv => exact DataPreserving_recreateView v hv
  | seq m₂ m₁ ih₂ ih₁ =>
    simp [SchemaMig.isViewCatalog] at h
    exact DataPreserving_seq m₂ m₁ (ih₂ h.1) (ih₁ h.2)
  | createTable | dropTable | renameTable | addColumn | dropColumn | renameColumn =>
    simp [SchemaMig.isViewCatalog] at h

/-! ## Dependency resolution along gated steps

Gated create/drop preserve `Schema.ViewDepsResolved` when the source is
resolved (see `Coupling`). Re-stated here as the policy surface.
-/

/-- Source resolved deps imply target resolved deps for this step. -/
def PreservesResolvedDeps {s₀ s₁ : Schema} (_m : SchemaMig s₀ s₁) : Prop :=
  Schema.ViewDepsResolved s₀ → Schema.ViewDepsResolved s₁

theorem PreservesResolvedDeps_dropView {s : Schema} (n : String)
    (h : NoDependentView s n) :
    PreservesResolvedDeps (SchemaMig.dropView (s := s) n h) :=
  fun hres => Schema.viewDepsResolved_dropView s n hres h

theorem PreservesResolvedDeps_dropTable {s : Schema} (n : String)
    (h : NoDependentView s n) :
    PreservesResolvedDeps (SchemaMig.dropTable (s := s) n h) :=
  fun hres => Schema.viewDepsResolved_dropTable s n hres h

theorem PreservesResolvedDeps_createView {s : Schema} (v : View)
    (h : CanCreateView s v) :
    PreservesResolvedDeps (SchemaMig.createView (s := s) v h) :=
  fun hres => Schema.viewDepsResolved_addView s v hres h

theorem PreservesResolvedDeps_id (s : Schema) :
    PreservesResolvedDeps (SchemaMig.id (s := s)) :=
  fun h => h

theorem PreservesResolvedDeps_seq {s₀ s₁ s₂ : Schema}
    (m₂ : SchemaMig s₁ s₂) (m₁ : SchemaMig s₀ s₁)
    (h₂ : PreservesResolvedDeps m₂) (h₁ : PreservesResolvedDeps m₁) :
    PreservesResolvedDeps (.seq m₂ m₁) :=
  fun h0 => h₂ (h₁ h0)

/-! ## Admissible paths

A path is **dep-safe** when every prefix stays `ViewDepsResolved`.
This is the path-level policy for gated catalog/table teardown without
requiring full `WellFormed` proofs for every free table operator.

Full `WellFormed` chains remain a strengthening for later.
-/

/--
  Path policy: start with resolved view deps; each step preserves that.
-/
inductive DepSafePath : {s₀ s₁ : Schema} → MigPath s₀ s₁ → Prop where
  | nil {s : Schema} (h : Schema.ViewDepsResolved s) :
      DepSafePath (.nil (s := s))
  | cons {s₀ s₁ s₂ : Schema}
      (m : SchemaMig s₀ s₁) (rest : MigPath s₁ s₂)
      (h0 : Schema.ViewDepsResolved s₀)
      (hstep : PreservesResolvedDeps m)
      (hrest : DepSafePath rest) :
      DepSafePath (.cons m rest)

namespace DepSafePath

theorem resolved_start {s₀ s₁ : Schema} {p : MigPath s₀ s₁}
    (h : DepSafePath p) : Schema.ViewDepsResolved s₀ := by
  cases h with
  | nil h => exact h
  | cons _ _ h0 _ _ => exact h0

/-- Flattened morphism of a dep-safe path preserves resolved deps. -/
theorem preserves_flatten {s₀ s₁ : Schema} {p : MigPath s₀ s₁}
    (h : DepSafePath p) : PreservesResolvedDeps (MigPath.flatten p) := by
  induction h with
  | nil => exact PreservesResolvedDeps_id _
  | cons m rest h0 hstep hrest ih =>
    intro hres
    simpa [MigPath.flatten] using
      (PreservesResolvedDeps_seq (MigPath.flatten rest) m ih hstep) hres

end DepSafePath

/-! ## Catalog vs stored data

`conforms` only requires base tables to appear in the instance.
View-catalog (data-preserving) steps leave the instance unchanged.
-/

/-- Data-preserving morphisms leave table conformance of `db` unchanged
    relative to the **source** schema (instance is identical). -/
theorem conforms_stable_under_DataPreserving {s₀ s₁ : Schema}
    (m : SchemaMig s₀ s₁) (h : DataPreserving m) (db : Instance)
    (hc : conforms db s₀) : conforms (applyMig m db) s₀ := by
  simpa [h db] using hc

/-- View-catalog steps are data-preserving, hence do not alter instances. -/
theorem applyMig_eq_id_of_isViewCatalog {s₀ s₁ : Schema}
    (m : SchemaMig s₀ s₁) (h : SchemaMig.isViewCatalog m = true) (db : Instance) :
    applyMig m db = db :=
  DataPreserving_of_isViewCatalog m h db

/-! ## Policy summary (for implementors)

1. **Functoriality** — `applyMig` preserves `id` and sequential composition
   (`Laws.lean`).
2. **View catalog = data-preserving** — `DataPreserving_of_isViewCatalog`.
3. **Drops are dependency-gated** — `dropView` / `dropTable` require
   `NoDependentView` (constructors).
4. **Creates are closed** — `createView` / `recreateView` require `Can*`.
5. **Dep-safe paths** — `DepSafePath` packages resolved-deps preservation
   along a `MigPath`.
6. **Instances store tables** — views are catalog-only; `conforms` is about
   tables; pure view steps do not rewrite rows.
-/

end MigrationAlgebra
