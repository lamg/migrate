[<MigLib.Dsl.Attributes.GeneratedDbNamespace("ExampleApp")>]
module ExampleMigSchema.MigSchema

open MigLib.Dsl.Attributes

[<AutoIncPK "id">]
[<Unique "name">]
[<Default("age", "18")>]
[<SelectBy "name">]
[<SelectLike "name">]
[<SelectByOrInsert "name">]
[<SelectOne>]
[<InsertOrIgnore>]
[<DeleteAll>]
[<DeleteWhere("Adult", "age >= @age")>]
[<Upsert>]
type Student = { id: int64; name: string; age: int64 }
