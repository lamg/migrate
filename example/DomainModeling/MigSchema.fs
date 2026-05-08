[<MigLib.Dsl.Attributes.GeneratedDbNamespace("ExampleApp")>]
module ExampleDomainModeling.MigSchema

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
[<Upsert>]
type Student = { id: int64; name: string; age: int64 }
