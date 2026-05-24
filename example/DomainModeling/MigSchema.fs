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

type StudentOpt = WithAddress of Student * address: string

[<ViewSql "
SELECT id, name, age FROM student
WHERE age >= 18
">]
type Student18 = { id: int64; name: string; age: int64 }

[<ViewSql "
SELECT id, name, age FROM student18
WHERE name like 'A%'
">]
type Student18A = { id: int64; name: string; age: int64 }
