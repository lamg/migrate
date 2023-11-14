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

module SqlParser.Scalar

open FParsec
open SqlParser.Types
open Basic

module K = Keyword
module S = Symbol

let integer: Parser<Expr, unit> = pint32 |>> Integer |> token "integer"

let string: Parser<Expr, unit> =
  let q = (pstring "'")

  between q q (manySatisfy (fun c -> c <> '\'')) |>> String |> token "string"

let envVar = var |>> EnvVar
let literal = integer <|> string <|> envVar

let rec caseWhen inner =
  parse {
    do! keyword K.Case
    do! keyword K.When
    let! cond = scalarOp inner
    do! keyword K.Then
    let! x = scalarOp inner
    do! keyword K.Else
    let! y = scalarOp inner
    do! keyword K.End

    return
      CaseWhen
        { cond = cond
          thenExpr = x
          elseExpr = y }
  }

and window =
  let partitionBy =
    keyword K.Partition
    >>. keyword K.By
    >>. sepBy1End var (symbol S.Comma) (opt orderBy)
    <|> (orderBy >>= fun x -> preturn ([], Some x))

  parse {
    do! keyword K.Over
    let! partition, order = parens partitionBy

    return
      { partitionBy = partition
        orderBy = order }
  }

and columnOrFunc select =
  parse {
    let! id = var

    let! args =
      parse {

        let! xs =
          sepBy (token "function arg" (caseWhen select <|> scalar (parens select))) (symbol S.Comma)
          |> parens

        let! w = opt window
        return (xs, w)
      }
      |> opt

    let! r =
      match args with
      | Some(xs, w) ->
        if id.qualifier |> Option.isSome then
          fail "Invalid character '.' in function name"
        else
          Func
            { name = id.``member``
              args = xs
              window = w }
          |> preturn
      | None -> Column id |> preturn

    return r
  }
  <?> "column or function"

and exists inner =
  parse {
    do! keyword K.Exists
    let! x = parens inner
    return Exists(SubQuery x)
  }

and notOp inner =
  parse {
    do! keyword K.Not
    let! x = scalar inner <|> parens (scalarOp inner)
    return Not x
  }

and scalar inner =
  integer
  <|> string
  <|> notOp inner
  <|> exists inner
  <|> (inner |>> SubQuery)
  <|> columnOrFunc inner
  <?> "scalar"

and scalarOp (inner: Parser<WithSelect, unit>) =
  let orOp = keyword K.Or >>. preturn (fun x y -> Or { left = x; right = y })
  let andOp = keyword K.And >>. preturn (fun x y -> And { left = x; right = y })
  let eqOp = symbol S.Eq >>. preturn (fun x y -> Eq { left = x; right = y })
  let gtOp = symbol S.Gt >>. preturn (fun x y -> Gt { left = x; right = y })
  let lteOp = symbol S.Lte >>. preturn (fun x y -> Lte { left = x; right = y })
  let likeOp = keyword K.Like >>. preturn (fun x y -> Like { left = x; right = y })
  let inOp = keyword K.In >>. preturn (fun x y -> In { left = x; right = y })

  let notIn =
    keyword K.Not
    >>. keyword K.In
    >>. preturn (fun x y -> Not(In { left = x; right = y }))

  let concatOp =
    symbol S.Concat >>. preturn (fun x y -> Concat { left = x; right = y })

  let ops = [ orOp; andOp; inOp; notIn; eqOp; gtOp; lteOp; likeOp; concatOp ]
  let term = scalar inner
  opParser ops term
