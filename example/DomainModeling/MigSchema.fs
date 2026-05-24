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
type Student = { id: int64; name: string; age: int64 }

type StudentOpt =
| WithAddress of Student * address:string
