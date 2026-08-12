/-
  Schema objects for the migration algebra.

  A schema has two kinds of relations:
  * base **tables** (stored)
  * **views** (derived: name, exposed columns, dependency names)

  View dependencies are name-level edges for well-formedness and
  acyclicity (Phase 1). No query bodies in this model.
-/

namespace MigrationAlgebra

/-- Minimal column type vocabulary (extend as needed). -/
inductive SqlType where
  | integer
  | text
  | boolean
  | real
  | blob
  deriving DecidableEq, Repr, Inhabited

/-- A column declaration. -/
structure Column where
  name     : String
  ty       : SqlType
  nullable : Bool := true
  deriving DecidableEq, Repr, Inhabited

/-- A base table declaration (name + ordered columns). -/
structure Table where
  name : String
  cols : List Column
  deriving DecidableEq, Repr, Inhabited

namespace Table

def colNames (t : Table) : List String :=
  t.cols.map (·.name)

def hasColumn (t : Table) (n : String) : Bool :=
  t.cols.any (·.name == n)

def findColumn (t : Table) (n : String) : Option Column :=
  t.cols.find? (·.name == n)

def addColumn (t : Table) (c : Column) : Table :=
  { t with cols := t.cols ++ [c] }

def dropColumn (t : Table) (n : String) : Table :=
  { t with cols := t.cols.filter (·.name != n) }

def renameColumn (t : Table) (fromName toName : String) : Table :=
  { t with
    cols := t.cols.map fun c =>
      if c.name == fromName then { c with name := toName } else c }

end Table

/--
  A view: derived relation.

  * `cols` — exposed shape (what clients / codegen see)
  * `deps` — names of tables or views this view reads (Phase 1; not column-level)
-/
structure View where
  name : String
  cols : List Column
  deps : List String := []
  deriving DecidableEq, Repr, Inhabited

namespace View

def colNames (v : View) : List String :=
  v.cols.map (·.name)

end View

/-- Schema: base tables plus catalog views. -/
structure Schema where
  tables : List Table := []
  views  : List View := []
  deriving DecidableEq, Repr, Inhabited

namespace Schema

def empty : Schema := {}

def tableNames (s : Schema) : List String :=
  s.tables.map (·.name)

def viewNames (s : Schema) : List String :=
  s.views.map (·.name)

/-- Shared relation namespace: tables and views together. -/
def relationNames (s : Schema) : List String :=
  s.tableNames ++ s.viewNames

def hasTable (s : Schema) (n : String) : Bool :=
  s.tables.any (·.name == n)

def hasView (s : Schema) (n : String) : Bool :=
  s.views.any (·.name == n)

def hasRelation (s : Schema) (n : String) : Bool :=
  s.hasTable n || s.hasView n

def findTable (s : Schema) (n : String) : Option Table :=
  s.tables.find? (·.name == n)

def findView (s : Schema) (n : String) : Option View :=
  s.views.find? (·.name == n)

def addTable (s : Schema) (t : Table) : Schema :=
  { s with tables := s.tables ++ [t] }

def dropTable (s : Schema) (n : String) : Schema :=
  { s with tables := s.tables.filter (·.name != n) }

def renameTable (s : Schema) (fromName toName : String) : Schema :=
  { s with
    tables := s.tables.map fun t =>
      if t.name == fromName then { t with name := toName } else t }

/-- Replace the table named `n` if present; otherwise leave tables unchanged. -/
def updateTable (s : Schema) (n : String) (f : Table → Table) : Schema :=
  { s with
    tables := s.tables.map fun t => if t.name == n then f t else t }

def addView (s : Schema) (v : View) : Schema :=
  { s with views := s.views ++ [v] }

def dropView (s : Schema) (n : String) : Schema :=
  { s with views := s.views.filter (·.name != n) }

/-- Insert or replace a view by name (used for `recreateView`). -/
def upsertView (s : Schema) (v : View) : Schema :=
  if s.hasView v.name then
    { s with
      views := s.views.map fun w => if w.name == v.name then v else w }
  else
    s.addView v

/--
  Every view dependency names a table or view in the schema.
-/
def ViewDepsResolved (s : Schema) : Prop :=
  ∀ v ∈ s.views, ∀ d ∈ v.deps, d ∈ s.relationNames

/--
  View list is a topological order of the view→view dependency graph:
  if view `v` depends on view `d`, then `d` appears earlier in `s.views`.

  This is a convenient witness of acyclicity (Phase 1).
-/
def ViewsTopoOrdered (s : Schema) : Prop :=
  ∀ i : Nat, i < s.views.length →
    ∀ d ∈ s.views[i]!.deps,
      (s.findView d).isNone ∨
        ∃ j : Nat, j < i ∧ s.views[j]!.name = d

/--
  Well-formed schema:
  * unique relation names (tables and views share a namespace)
  * unique column names within each relation
  * view dependencies resolve
  * view dependencies among views are acyclic (topo-ordered list)
-/
def WellFormed (s : Schema) : Prop :=
  s.relationNames.Nodup ∧
    (∀ t ∈ s.tables, t.colNames.Nodup) ∧
    (∀ v ∈ s.views, v.colNames.Nodup) ∧
    ViewDepsResolved s ∧
    ViewsTopoOrdered s

end Schema

/-! ### Phase 2: dependency gates (not “drop all views”) -/

/--
  No view in the catalog lists `n` as a dependency.

  Required to **drop** relation `n` (table or view): nothing may still point at it.
  This is the algebraic gate; a path may drop several views in order to
  discharge a chain of such gates.
-/
def NoDependentView (s : Schema) (n : String) : Prop :=
  ∀ v ∈ s.views, n ∉ v.deps

/-- Boolean checker for `NoDependentView` (for `decide` / examples). -/
def noDependentViewb (s : Schema) (n : String) : Bool :=
  s.views.all fun v => !v.deps.contains n

theorem noDependentViewb_eq (s : Schema) (n : String) :
    noDependentViewb s n = true ↔ NoDependentView s n := by
  simp [noDependentViewb, NoDependentView, List.all_eq_true]

instance (s : Schema) (n : String) : Decidable (NoDependentView s n) :=
  decidable_of_iff _ (noDependentViewb_eq s n)

/--
  Side conditions to **create** a view: fresh name, deps already in the catalog,
  unique column names on the view.
-/
def CanCreateView (s : Schema) (v : View) : Prop :=
  v.name ∉ s.relationNames ∧
    (∀ d ∈ v.deps, d ∈ s.relationNames) ∧
    v.colNames.Nodup

/--
  Side conditions to **recreate** a view: name already a view, new deps resolve
  in the current catalog, unique columns.
-/
def CanRecreateView (s : Schema) (v : View) : Prop :=
  v.name ∈ s.viewNames ∧
    (∀ d ∈ v.deps, d ∈ s.relationNames) ∧
    v.colNames.Nodup

end MigrationAlgebra
