module ExampleSchema.MigSchema

open MigLib.Db.Attributes

[<AutoIncPK "id">]
[<Unique "name">]
[<Default("age", "18")>]
[<SelectBy "name">]
[<SelectLike "name">]
[<SelectByOrInsert "name">]
[<SelectOne>]
[<InsertOrIgnore>]
[<DeleteAll>]
[<Upsert>]
type Student = { id: int64; name: string; age: int64 }
