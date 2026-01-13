module Test.DUTypeExtensionTest

open Xunit

// Test that F# allows type extensions on discriminated unions
[<RequireQualifiedAccess>]
type TestDU =
  | Base of {| Id: int; Name: string |}
  | WithExtra of
    {| Id: int
       Name: string
       Extra: string |}

// Type extension with static methods on DU
type TestDU with
  static member Create(name: string) = TestDU.Base {| Id = 1; Name = name |}

  static member GetName(du: TestDU) =
    match du with
    | TestDU.Base data -> data.Name
    | TestDU.WithExtra data -> data.Name

[<Fact>]
let ``F# allows type extensions on discriminated unions`` () =
  let testDU = TestDU.Create "Alice"
  let name = TestDU.GetName testDU
  Assert.Equal("Alice", name)

[<Fact>]
let ``Type extensions work with pattern matching`` () =
  let baseCase = TestDU.Base {| Id = 1; Name = "Bob" |}

  let extraCase =
    TestDU.WithExtra
      {| Id = 2
         Name = "Carol"
         Extra = "extra" |}

  Assert.Equal("Bob", TestDU.GetName baseCase)
  Assert.Equal("Carol", TestDU.GetName extraCase)
