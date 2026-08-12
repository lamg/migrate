/-
  Schema objects for the migration algebra.

  A schema is a finite collection of named tables; each table is a finite
  collection of named columns with a SQL-ish type and nullability.
-/

namespace MigrationAlgebra

/-- Minimal SQL type vocabulary (extend as needed). -/
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

/-- A table declaration (name + ordered columns). -/
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

/-- A schema: named tables (list; uniqueness is a well-formedness predicate). -/
structure Schema where
  tables : List Table
  deriving DecidableEq, Repr, Inhabited

namespace Schema

def empty : Schema := ⟨[]⟩

def tableNames (s : Schema) : List String :=
  s.tables.map (·.name)

def hasTable (s : Schema) (n : String) : Bool :=
  s.tables.any (·.name == n)

def findTable (s : Schema) (n : String) : Option Table :=
  s.tables.find? (·.name == n)

def addTable (s : Schema) (t : Table) : Schema :=
  { tables := s.tables ++ [t] }

def dropTable (s : Schema) (n : String) : Schema :=
  { tables := s.tables.filter (·.name != n) }

def renameTable (s : Schema) (fromName toName : String) : Schema :=
  { tables := s.tables.map fun t =>
      if t.name == fromName then { t with name := toName } else t }

/-- Replace the table named `n` if present; otherwise leave the schema unchanged. -/
def updateTable (s : Schema) (n : String) (f : Table → Table) : Schema :=
  { tables := s.tables.map fun t => if t.name == n then f t else t }

/--
  Well-formedness: table names unique, column names unique within each table.
  Migrations are only required to preserve this on successful paths.
-/
def WellFormed (s : Schema) : Prop :=
  s.tableNames.Nodup ∧ ∀ t ∈ s.tables, t.colNames.Nodup

end Schema

end MigrationAlgebra
