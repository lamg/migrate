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

[<PK "id">]
type SeededStudent = { id: int64; name: string }

[<PK("tenantId", "externalId")>]
type SeededTenantUser =
  { tenantId: string
    externalId: string
    name: string }

[<AutoIncPK "id">]
type SeededTenantSession =
  { id: int64
    user: SeededTenantUser
    token: string }

let alice = { id = 1L; name = "Alice" }

let bob = { id = 2L; name = "Bob" }

let tenantAlice =
  { tenantId = "tenant-a"
    externalId = "user-1"
    name = "Tenant Alice" }

let tenantAliceSession =
  { id = 10L
    user = tenantAlice
    token = "session-1" }

let ignoredSeedMarker = "not-a-seed"
