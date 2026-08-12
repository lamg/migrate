/-
  Denotational sketch: schemas classify *instances* (databases), and a
  migration denotes a function on instances.

  This is intentionally simplified (stringly-typed rows, no FK graph, no SQL
  evaluation). It is enough to state soundness shapes and prove them for the
  atomic constructors.
-/

import MigrationAlgebra.Schema
import MigrationAlgebra.SchemaMig

namespace MigrationAlgebra

/-- A cell value in the toy instance model. -/
inductive Value where
  | null
  | int  : Int → Value
  | text : String → Value
  | bool : Bool → Value
  | real : String → Value   -- decimal text for simplicity (avoid Float Repr issues)
  | blob : List UInt8 → Value
  deriving Repr

/-- A row: map from column name to value (association list). -/
abbrev Row := List (String × Value)

/-- Table instance: unordered bag of rows (list for simplicity). -/
abbrev TableData := List Row

/-- Database instance: association list table-name ↦ data. -/
structure Instance where
  data : List (String × TableData)
  deriving Repr, Inhabited

namespace Instance

def empty : Instance := ⟨[]⟩

def tables (db : Instance) : List String :=
  db.data.map (·.1)

def find (db : Instance) (table : String) : Option TableData :=
  (db.data.find? (·.1 == table)).map (·.2)

def upsert (db : Instance) (table : String) (td : TableData) : Instance :=
  if db.data.any (·.1 == table) then
    ⟨db.data.map fun (n, d) => if n == table then (n, td) else (n, d)⟩
  else
    ⟨db.data ++ [(table, td)]⟩

def erase (db : Instance) (table : String) : Instance :=
  ⟨db.data.filter (·.1 != table)⟩

def renameTable (db : Instance) (fromName toName : String) : Instance :=
  ⟨db.data.map fun (n, d) => if n == fromName then (toName, d) else (n, d)⟩

/-- Drop a column from every row of a table. -/
def dropColumn (db : Instance) (table col : String) : Instance :=
  match db.find table with
  | none => db
  | some td =>
    let td' := td.map fun row => row.filter (·.1 != col)
    db.upsert table td'

/-- Rename a column in every row of a table. -/
def renameColumn (db : Instance) (table fromName toName : String) : Instance :=
  match db.find table with
  | none => db
  | some td =>
    let td' := td.map fun row =>
      row.map fun (n, v) => if n == fromName then (toName, v) else (n, v)
    db.upsert table td'

/-- Add a column filled with NULL on every existing row. -/
def addColumn (db : Instance) (table col : String) : Instance :=
  match db.find table with
  | none => db
  | some td =>
    let td' := td.map fun row =>
      if row.any (·.1 == col) then row else row ++ [(col, Value.null)]
    db.upsert table td'

end Instance

/--
  Apply a schema morphism to an instance.

  Base-table ops reshape stored rows. View catalog ops (`createView`,
  `dropView`, `recreateView`) are **identity on instances**: views are not
  stored row bags in this model (Phase 1).

  Missing tables are treated leniently (no-op) so partial instances still
  transform; a stricter model can require `conforms db s₀` as a precondition.
-/
def applyMig {s₀ s₁ : Schema} : SchemaMig s₀ s₁ → Instance → Instance
  | .id, db => db
  | .createTable t, db => db.upsert t.name []
  | .dropTable n, db => db.erase n
  | .renameTable fr to, db => db.renameTable fr to
  | .addColumn table c, db => db.addColumn table c.name
  | .dropColumn table col, db => db.dropColumn table col
  | .renameColumn table fr to, db => db.renameColumn table fr to
  | .createView _, db => db
  | .dropView _, db => db
  | .recreateView _, db => db
  | .seq m₂ m₁, db => applyMig m₂ (applyMig m₁ db)

/-- Optional conformance: every schema **table** exists in the instance.
    Views are catalog-only and are not required as instance keys. -/
def conforms (db : Instance) (s : Schema) : Prop :=
  ∀ t ∈ s.tables, (db.find t.name).isSome

end MigrationAlgebra
