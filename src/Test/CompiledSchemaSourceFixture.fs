module CompiledSchemaSourceFixture

open MigLib.Db.Attributes

type Marker = class end

[<AutoIncPK "id">]
[<SelectBy "name">]
type Student = { id: int64; name: string }
