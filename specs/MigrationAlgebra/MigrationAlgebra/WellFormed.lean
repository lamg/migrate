/-
  Full `WellFormed` preservation for gated catalog/table steps.
-/

import MigrationAlgebra.Schema
import MigrationAlgebra.Coupling

namespace MigrationAlgebra

theorem map_filter_tableName (ts : List Table) (n : String) :
    (ts.filter (fun t => t.name != n)).map (·.name) =
      (ts.map (·.name)).filter (fun x => x != n) := by
  induction ts with
  | nil => simp
  | cons t ts ih =>
    simp [List.filter]
    split <;> simp [ih]

theorem map_filter_viewName (vs : List View) (n : String) :
    (vs.filter (fun v => v.name != n)).map (·.name) =
      (vs.map (·.name)).filter (fun x => x != n) := by
  induction vs with
  | nil => simp
  | cons v vs ih =>
    simp [List.filter]
    split <;> simp [ih]

theorem map_filter_colName (cs : List Column) (n : String) :
    (cs.filter (fun c => c.name != n)).map (·.name) =
      (cs.map (·.name)).filter (fun x => x != n) := by
  induction cs with
  | nil => simp
  | cons c cs ih =>
    simp [List.filter]
    split <;> simp [ih]

namespace Schema

theorem wellFormed_dropTable (s : Schema) (n : String)
    (hwf : WellFormed s) (hnd : NoDependentView s n) :
    WellFormed (s.dropTable n) := by
  obtain ⟨hnod, ht, hv, hres, htopo⟩ := hwf
  refine ⟨?nodup, ?tcols, hv, viewDepsResolved_dropTable s n hres hnd, ?topo⟩
  · have heq :
        (s.dropTable n).relationNames =
          s.tableNames.filter (fun x => x != n) ++ s.viewNames := by
      simp [relationNames, tableNames, viewNames, dropTable, map_filter_tableName]
    rw [heq]
    have hpair : (s.tableNames ++ s.viewNames).Nodup := by
      simpa [relationNames] using hnod
    rw [List.nodup_append] at hpair ⊢
    refine ⟨List.Nodup.sublist List.filter_sublist hpair.1, hpair.2.1, ?_⟩
    intro a ha b hb
    exact hpair.2.2 a (List.mem_filter.mp ha).1 b hb
  · intro t ht'
    exact ht t (by
      simp [dropTable] at ht'
      exact ht'.1)
  · intro pref rest v heq d hd
    have heq' : s.views = pref ++ v :: rest := by simpa [dropTable] using heq
    exact htopo pref rest v heq' d hd

theorem wellFormed_dropColumn (s : Schema) (tableName colName : String)
    (hwf : WellFormed s) (_hnd : NoDependentView s tableName) :
    WellFormed (s.updateTable tableName (fun t => t.dropColumn colName)) := by
  obtain ⟨hnod, ht, hv, hres, htopo⟩ := hwf
  have hrel :
      (s.updateTable tableName (fun t => t.dropColumn colName)).relationNames =
        s.relationNames := by
    simp [relationNames, tableNames, viewNames, updateTable]
    intro t _
    split <;> rfl
  refine ⟨by simpa [hrel] using hnod, ?_, hv, ?_, ?_⟩
  · intro t ht'
    simp [updateTable] at ht'
    obtain ⟨t0, ht0, rfl⟩ := ht'
    have h0 := ht t0 ht0
    split
    · simp [Table.dropColumn, Table.colNames, map_filter_colName]
      exact List.Nodup.sublist List.filter_sublist (by simpa [Table.colNames] using h0)
    · exact h0
  · intro v hv' d hd
    have hvS : v ∈ s.views := by simpa [updateTable] using hv'
    have := hres v hvS d hd
    simpa [hrel] using this
  · intro pref rest v heq d hd
    have heq' : s.views = pref ++ v :: rest := by simpa [updateTable] using heq
    simpa [findView, updateTable] using htopo pref rest v heq' d hd

end Schema
end MigrationAlgebra

