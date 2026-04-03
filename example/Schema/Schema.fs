module Schema

open MigLib.Db

[<AutoIncPK "id">]
[<Unique "name">]
[<Default("age", "18")>]
[<SelectAll>]
[<SelectBy "name">]
[<DeleteAll>]
type Student = { id: int64; name: string; age: int64 }
