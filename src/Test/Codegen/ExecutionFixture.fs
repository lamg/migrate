module TestCodegenSchema.MigSchema

open MigLib.Dsl.Attributes

type Marker = class end

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
type CodegenFixture = { id: int64; name: string; age: int64 }

[<PK "id">]
[<SelectBy "name">]
[<SelectByOrInsert "name">]
[<SelectOne>]
[<InsertOrIgnore>]
[<DeleteAll>]
type Person = { id: string; name: string }

type PersonExt = Email of Person * email: string

[<ViewSql "SELECT id, name FROM codegen_fixture">]
[<SelectBy "name">]
[<SelectOne>]
type CodegenFixtureView = { id: int64; name: string }

let fixtureSeed: CodegenFixture = { id = 0L; name = "seed"; age = 18L }

let personSeed: Person = { id = "person-1"; name = "Pat" }
