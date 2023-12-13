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

module internal Migrate.SqlParser.Basic

open FParsec
open Microsoft.FSharp.Collections
open Migrate.SqlParser.Types

module K = Keyword
module S = Symbol

let spaceComments: Parser<unit, unit> =
  parse {
    do! spaces
    let! _ = many (skipString "--" >>. restOfLine true >>. spaces)
    return ()
  }
  <?> "space or comment"

let token name p =
  parse {
    let! r = p
    do! spaceComments
    return r
  }
  <?> $"token {name}"

let skipToken s = token s (skipStringCI s)

/// <summary>
/// Parses a symbol and its trailing spaces or comments.
/// </summary>
/// <param name="x"></param>
let symbol (x: S.Symbol) =
  let s = S.string[x]
  token s (skipString s)

let symbolWitness (x: S.Symbol) = symbol x >>. preturn x

/// <summary>
/// Parses a symbol without any trailing spaces or comments.
/// </summary>
/// <param name="x"></param>
let symbolExact (x: Symbol.Symbol) =
  let s = Symbol.string[x]
  skipStringCI s

/// <summary>
/// Parses a keyword, case insensitive, with its trailing spaces or comments
/// </summary>
/// <param name="k"></param>
let keyword (k: Keyword.Keyword) : Parser<unit, unit> =
  let capK = k.ToString().ToUpper()

  attempt (
    capK |> skipStringCI
    >>. (spaces1
         <|> eof
         <|> (followedBy (symbol S.Semicolon <|> symbol S.OPar <|> symbol S.CPar <|> symbol S.Comma)))
  )
  <?> $"keyword {capK}"

let clauseHead (k: Keyword.Keyword) =
  parse {
    do! keyword k
    return k
  }
  <?> $"clause head {k}"

let identSegment: Parser<string, unit> =
  parse {
    let! x = letter
    let! xs = many (letter <|> digit <|> (satisfy (fun c -> c = '_')))

    let id = (x :: xs |> System.String.Concat)
    return id
  }

let ident: Parser<string, unit> =
  token
    "identifier"
    (identSegment
     <|> between (symbolExact S.DoubleQuote) (symbolExact S.DoubleQuote) identSegment)

let var: Parser<Var, unit> =
  let id =
    parse {
      let! left = identSegment
      let! right = opt (symbolExact S.Dot >>. identSegment)

      let id =
        match right with
        | Some r ->
          { qualifier = Some left
            ``member`` = r }
        | None -> { qualifier = None; ``member`` = left }

      return id
    }

  let dq = symbolExact S.DoubleQuote
  let idQuote = id <|> between dq dq id
  idQuote |> token "identifier"

/// <summary>
/// Parses a sequence of at least one element, separated by commas.
/// </summary>
/// <param name="p">element parser</param>
let sep1Comma p = sepBy1 p (symbol S.Comma)

/// <summary>
/// Parses a parenthesized expression.
/// </summary>
/// <param name="p">expression parser</param>
let parens (p: Parser<'a, unit>) =
  between (symbol S.OPar) (symbol S.CPar) p

type Either<'a, 'b> =
  | Left of 'a
  | Right of 'b

let sepBy1Cont (p: Parser<'a, unit>) (sepEnd: Parser<Either<'b, 'c>, unit>) =
  let rec loop (acc: 'a list) =
    parse {
      let! x = p
      let! d = sepEnd

      match d with
      | Left _ -> return! loop (x :: acc)
      | Right next ->
        let xs = List.rev (x :: acc)
        return (xs, next)
    }

  loop []

let sepBy1End (p: Parser<'a, unit>) (sep: Parser<'b, unit>) (endP: Parser<'c, unit>) =
  sepBy1Cont p (sep |>> Left <|> (endP |>> Right))

let opParser (ps: Parser<'a -> 'a -> 'a, unit> list) (term: Parser<'a, unit>) =
  let ips = ps |> List.mapi (fun i p -> p |>> (fun f -> (i, f)))

  let rec loop (stack: (int * ('a -> 'a)) list) =
    parse {
      let! x = term <|> (parens (loop []))
      let! op = opt (choice ips)

      match op with
      | Some(i, f) ->
        match stack with
        | (i', f') :: stack' ->
          if i >= i' then
            return! (i, f x) :: stack |> loop
          else
            return! (i, f (f' x)) :: stack' |> loop
        | [] -> return! loop [ i, f x ]

      | None -> return List.fold (fun acc (_, f) -> f acc) x stack
    }

  loop []

let composite (xs: K.Keyword list) =
  let rec loop (ys: K.Keyword list) =
    match ys with
    | [] -> preturn ()
    | y :: ys' ->
      parse {
        do! keyword y
        return! loop ys'
      }

  parse {
    do! loop xs
    return S.Composite xs
  }

let clauseEnd =
  symbolWitness S.Semicolon
  <|> (eof >>. preturn S.Semicolon)
  <|> (followedByL (symbol S.CPar) "( symbol" >>. preturn S.Semicolon)
  <|> (followedByL (keyword K.Union) "UNION keyword" >>. preturn S.Semicolon)

let followedByKeyword =
  followedBy (
    keyword K.From
    <|> keyword K.Where
    <|> keyword K.Group
    <|> keyword K.Order
    <|> keyword K.Having
    <|> keyword K.Limit
    <|> keyword K.Offset
    <|> keyword K.Union
  )

let headlessOrderBy =
  let ascDesc =
    (keyword K.Asc >>. preturn true)
    <|> (keyword K.Desc >>. preturn false)
    <|> (preturn true)

  parse {
    let! xs, asc = sepBy1End var (symbol S.Comma) ascDesc

    return { columns = xs; asc = asc }
  }
  <?> "order by"

let orderBy = composite [ K.Order; K.By ] >>. headlessOrderBy
