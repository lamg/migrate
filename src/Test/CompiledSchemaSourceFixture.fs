module CompiledSchemaSourceFixture

open MigLib.Dsl.Attributes

type Marker = class end

[<AutoIncPK "id">]
[<SelectBy "name">]
type Student = { id: int64; name: string }
