// Copyright 2023 Luis Ángel Méndez Gort

// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at

//     http://www.apache.org/licenses/LICENSE-2.0

// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

module Basic

open Xunit
open FParsec
open Util
open SqlParser.Types
open SqlParser.Basic

module K = SqlParser.Keyword
module S = SqlParser.Symbol


[<Fact>]
let SepBy1Cont () =
  let testName = "SepBy1Cont"
  let case0 = "a, b end", ([ "a"; "b" ], K.End)
  let case1 = "a, b )", "Expecting: keyword END"
  let case2 = "a, b when", ([ "a"; "b" ], K.When)

  let sep =
    parse {
      do! symbol S.Comma
      return Left()
    }
    <|> parse {
      do! keyword K.End
      return Right K.End
    }
    <|> parse {
      do! keyword K.When
      return Right K.When
    }

  let p = sepBy1Cont (spaces >>. identSegment .>> spaces) sep
  parseStatementTest testName p case0
  checkFailure testName p case1
  parseStatementTest testName p case2

[<Fact>]
let CommentTest () =
  let s = "*\n--a comment"
  let testName = "CommentTest"
  parseStatementTest testName (symbol S.Asterisk) (s, ())

[<Fact>]
let KeywordTest () =
  let s = "CASE"
  let testName = "KeywordTest"
  parseStatementTest testName (keyword K.Case) (s, ())

[<Fact>]
let KeywordFailTest () =
  let s = "CASE"
  let testName = "KeywordFailTest"
  checkFailure testName (keyword K.When) (s, "Expecting: keyword WHEN")

[<Fact>]
let IdentDotTest () =
  let testName = "IdentDotTest"

  let case0 =
    "a.b",
    { qualifier = Some "a"
      ``member`` = "b" }

  parseStatementTest testName var case0

[<Fact>]
let IdentTest () =
  let cases =
    [ "b", { qualifier = None; ``member`` = "b" }
      "a.b",
      { qualifier = Some "a"
        ``member`` = "b" }
      "a.b ",
      { qualifier = Some "a"
        ``member`` = "b" } ]

  let testName = "IdentNoDotTest"
  cases |> List.iteri (fun i -> parseStatementTest $"{testName}-{i}" var)

[<Fact>]
let SequenceTest () =
  let testName = "SequenceTest"
  let case0 = "*, *", [ "*"; "*" ]

  let p = pstring "*" |> sep1Comma

  parseStatementTest testName p case0


[<Fact>]
let ParensTest () =
  let s = "(*)", "*"
  let testName = "ParensTest"
  parseStatementTest testName (parens (pstring "*")) s

[<Fact>]
let SequenceEndTest () =
  let s = "coco, casa end", [ "coco"; "casa" ]
  let testName = "SequenceEndTest"
  let elem = spaces >>. identSegment .>> spaces
  let p = sepBy1End elem (symbol S.Comma) (keyword K.End) |>> fst
  parseStatementTest testName p s

[<Fact>]
let SequenceEndSingle () =
  let s = "coco end", [ "coco" ]
  let testName = "SequenceEndSingle"
  let elem = identSegment .>> spaces
  let p = sepBy1End elem (symbol S.Comma) (keyword K.End) |>> fst

  parseStatementTest testName p s

[<Fact>]
let OpParser () =
  let cases =
    [ "a", col "a"
      "a AND b", sand (col "a") (col "b")
      "a OR b", sor (col "a") (col "b")
      "a = b", eq (col "a") (col "b")
      "a OR b AND c", sor (col "a") (sand (col "b") (col "c"))
      "a OR b AND c = d", sor (col "a") (sand (col "b") (eq (col "c") (col "d")))
      "a OR b OR c", sor (col "a") (sor (col "b") (col "c"))
      "a AND b OR c", sor (sand (col "a") (col "b")) (col "c")
      "(a)", col "a"
      "a AND (b OR c)", sand (col "a") (sor (col "b") (col "c"))
      "a AND ((b OR c))", sand (col "a") (sor (col "b") (col "c")) ]

  let orOp = keyword K.Or >>. preturn (fun x y -> Or { left = x; right = y })
  let andOp = keyword K.And >>. preturn (fun x y -> And { left = x; right = y })
  let eqOp = symbol S.Eq >>. preturn (fun x y -> Eq { left = x; right = y })
  let gtOp = symbol S.Gt >>. preturn (fun x y -> Gt { left = x; right = y })
  let ops = [ orOp; andOp; eqOp; gtOp ]
  let term = var |>> Column

  cases
  |> List.iteri (fun i ->
    let testName = $"OpParser-{i}"
    parseStatementTest testName (opParser ops term))
