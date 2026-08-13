/-
  Phase 2: dependency coupling.

  Destructive removals are gated by `NoDependentView` on the morphism
  constructors (see `SchemaMig`). This module records easy structural facts
  and the intended preservation story.

  We intentionally do **not** introduce a “drop all views” primitive.
-/

import MigrationAlgebra.Schema
import MigrationAlgebra.SchemaMig

namespace MigrationAlgebra
namespace Schema

@[simp] theorem dropView_views (s : Schema) (n : String) :
    (s.dropView n).views = s.views.filter (·.name != n) :=
  rfl

@[simp] theorem dropTable_tables (s : Schema) (n : String) :
    (s.dropTable n).tables = s.tables.filter (·.name != n) :=
  rfl

@[simp] theorem dropTable_views (s : Schema) (n : String) :
    (s.dropTable n).views = s.views :=
  rfl

@[simp] theorem addView_views (s : Schema) (v : View) :
    (s.addView v).views = s.views ++ [v] :=
  rfl

/--
  After dropping view `n` under `NoDependentView s n`, every remaining view's
  deps avoid `n`, so they still resolve if they resolved before (only `n` left
  the name set among possible deps that could have broken).
-/
theorem mem_relationNames_dropView_of_ne (s : Schema) (n d : String)
    (hd : d ∈ s.relationNames) (hne : d ≠ n) :
    d ∈ (s.dropView n).relationNames := by
  simp [relationNames, tableNames, viewNames, dropView, List.mem_append, List.mem_map,
    List.mem_filter] at hd ⊢
  rcases hd with ⟨t, ht, rfl⟩ | ⟨w, hw, rfl⟩
  · exact Or.inl ⟨t, ht, rfl⟩
  · exact Or.inr ⟨w, ⟨hw, fun h => hne (by simp [h])⟩, rfl⟩

theorem mem_relationNames_dropTable_of_ne (s : Schema) (n d : String)
    (hd : d ∈ s.relationNames) (hne : d ≠ n) :
    d ∈ (s.dropTable n).relationNames := by
  simp [relationNames, tableNames, viewNames, dropTable, List.mem_append, List.mem_map,
    List.mem_filter] at hd ⊢
  rcases hd with ⟨t, ht, rfl⟩ | ⟨w, hw, rfl⟩
  · exact Or.inl ⟨t, ⟨ht, fun h => hne (by simp [h])⟩, rfl⟩
  · exact Or.inr ⟨w, hw, rfl⟩

theorem viewDepsResolved_dropView (s : Schema) (n : String)
    (hres : ViewDepsResolved s) (hnd : NoDependentView s n) :
    ViewDepsResolved (s.dropView n) := by
  intro v hv d hd
  have hvS : v ∈ s.views := (List.mem_filter.mp hv).1
  have hne : d ≠ n := fun heq => hnd v hvS (heq ▸ hd)
  exact mem_relationNames_dropView_of_ne s n d (hres v hvS d hd) hne

theorem viewDepsResolved_dropTable (s : Schema) (n : String)
    (hres : ViewDepsResolved s) (hnd : NoDependentView s n) :
    ViewDepsResolved (s.dropTable n) := by
  intro v hv d hd
  have hvS : v ∈ s.views := by simpa [dropTable] using hv
  have hne : d ≠ n := fun heq => hnd v hvS (heq ▸ hd)
  exact mem_relationNames_dropTable_of_ne s n d (hres v hvS d hd) hne

theorem viewDepsResolved_addView (s : Schema) (v : View)
    (hres : ViewDepsResolved s) (hcan : CanCreateView s v) :
    ViewDepsResolved (s.addView v) := by
  intro w hw d hd
  have hw' : w ∈ s.views ∨ w = v := by
    simp [addView, List.mem_append] at hw
    exact hw
  match hw' with
  | Or.inl hwS =>
    have := hres w hwS d hd
    simp [relationNames, tableNames, viewNames, addView, List.mem_append, List.mem_map] at this ⊢
    match this with
    | Or.inl h => exact Or.inl h
    | Or.inr h => exact Or.inr (Or.inl h)
  | Or.inr hwv =>
    subst hwv
    have := hcan.2.1 d hd
    simp [relationNames, tableNames, viewNames, addView, List.mem_append, List.mem_map] at this ⊢
    match this with
    | Or.inl h => exact Or.inl h
    | Or.inr h => exact Or.inr (Or.inl h)

end Schema
end MigrationAlgebra
