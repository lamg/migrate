module SchemaReflectionFixture

open MigLib.Db

[<Measure>]
type Byte

type Status =
  | Active
  | InProgress

[<AutoIncPK "id">]
type Student = { id: int64; name: string }

[<AutoIncPK "id">]
type Account = { id: int64; name: string }

[<AutoIncPK "id">]
type Invoice =
  { id: int64
    account: Account
    total: float }

[<AutoIncPK "id">]
[<SelectBy "status">]
type StudentWithStatus =
  { id: int64
    name: string
    status: Status }

[<ViewSql "SELECT id, status FROM student_with_status">]
[<SelectBy "status">]
type StudentStatusView = { id: int64; status: Status }

[<AutoIncPK "id">]
type File =
  { id: int64
    contentLength: int64<Byte>
    slug: string }
