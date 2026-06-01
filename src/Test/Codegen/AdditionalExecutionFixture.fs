[<MigLib.Dsl.Attributes.GeneratedDbNamespace("TestGeneratedDb")>]
module TestCodegenSchema.Accounting

open MigLib.Dsl.Attributes

[<AutoIncPK "id">]
[<SelectBy "name">]
type LedgerAccount = { id: int64; name: string }

[<AutoIncPK "id">]
[<SelectBy "owner_id">]
type Invoice =
  {
    id: int64
    owner: MigSchema.Person
    total: float
  }

let ledgerSeed: LedgerAccount = { id = 0L; name = "revenue" }

let invoiceSeed: Invoice =
  {
    id = 0L
    owner = MigSchema.personSeed
    total = 10.0
  }
