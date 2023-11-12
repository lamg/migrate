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

module SqlParser.Select

open FParsec
open SqlParser.Types
open Basic
open Scalar

module K = Keyword
module S = Symbol

#nowarn "40"



/// <summary>
/// Parse table or sub-query with optional alias, possibly detecting a join instead of an alias
/// </summary>
let aliasColumn select =
    let asAlias =
        parse {
            let! _ = opt (keyword K.As)

            return! (followedByKeyword >>. preturn None <|> (token "alias" ident |> opt))
        }

    parse {
        let! expr = scalarOp select
        do! spaceComments
        let! alias = asAlias

        match alias with
        | Some a -> return Alias { expr = expr; alias = a }
        | None -> return expr
    }

let aliasTable (select: Parser<WithSelect, unit>) =
    let asAlias =
        parse {
            let! _ = opt (keyword K.As)

            return! (followedByKeyword >>. preturn None <|> (token "alias" ident |> opt))
        }

    parse {
        let! table = var |>> Table <|> parens (select |>> SubQuery)
        do! spaceComments
        let! alias = asAlias

        match alias with
        | Some a -> return Alias { expr = table; alias = a }
        | None -> return table
    }

let joinOp select =
    let joinOp =
        (keyword K.Inner >>. keyword K.Join <|> keyword K.Join)
        >>. preturn (fun x y -> InnerJoin { left = x; right = y })

    let leftOuterJoinOp =
        keyword K.Left
        >>. keyword K.Outer
        >>. keyword K.Join
        >>. preturn (fun x y -> LeftOuterJoin { left = x; right = y })

    opParser [ joinOp; leftOuterJoinOp ] (aliasTable select)

let tableExpr select =
    parse {
        let! join = joinOp select
        let! on = opt (keyword K.On >>. scalarOp pzero)

        match on with
        | None -> return join
        | Some on -> return JoinOn { relation = join; onExpr = on }
    }

let fromExpr select =
    sepBy1End
        (tableExpr select)
        (symbol S.Comma)
        (composite [ K.Where ]
         <|> composite [ K.Group; K.By ]
         <|> composite [ K.Order; K.By ]
         <|> composite [ K.Limit ]
         <|> clauseEnd)
    <?> "from clause"

let groupByExpr =
    sepBy1End
        var
        (symbol S.Comma)
        (composite [ K.Having ]
         <|> composite [ K.Limit ]
         <|> composite [ K.Order; K.By ]
         <|> clauseEnd)

let columnsExpr select =
    let endColumns = composite [ K.From ] <|> clauseEnd

    parse {
        do! symbol S.Asterisk
        let! endP = endColumns
        return [], endP
    }
    <|> sepBy1End (aliasColumn select) (symbol S.Comma) endColumns

let headlessSelect select =
    parse {
        let! distinct = opt (keyword K.Distinct)

        let! columns, endCol = columnsExpr select

        let! from, endFrom =
            match endCol with
            | S.Composite [ K.From ] -> fromExpr select
            | _ -> preturn ([], endCol)

        let! where, endWhere =
            match endFrom with
            | S.Composite [ K.Where ] ->
                parse {
                    let! cond = opt <| scalarOp select

                    let! endP =
                        composite [ K.Group; K.By ]
                        <|> composite [ K.Order; K.By ]
                        <|> composite [ K.Limit ]
                        <|> clauseEnd

                    return cond, endP
                }
            | _ -> preturn (None, endFrom)

        let! groupBy, endGroupBy =
            match endWhere with
            | S.Composite [ K.Group; K.By ] -> groupByExpr
            | _ -> preturn ([], endWhere)

        let! having, endHaving =
            match endGroupBy with
            | S.Composite [ K.Having ] ->
                parse {
                    let! cond = opt <| scalarOp select
                    let! endP = composite [ K.Order; K.By ] <|> clauseEnd
                    return cond, endP
                }
            | _ -> preturn (None, endGroupBy)

        let! orderBy, endOrderBy =
            match endHaving with
            | S.Composite [ K.Order; K.By ] ->

                parse {
                    let! r = headlessOrderBy
                    let! endP = composite [ K.Limit ] <|> clauseEnd
                    return Some r, endP
                }
            | _ -> preturn (None, endHaving)

        let! limit, endLimit =
            match endOrderBy with
            | S.Composite [ K.Limit ] ->
                parse {
                    let! r = pint32 |> token "integer"
                    let! endP = composite [ K.Offset ] <|> clauseEnd
                    return Some r, endP
                }
            | _ -> preturn (None, endOrderBy)

        let! offset =
            match endLimit with
            | S.Composite [ K.Offset ] ->
                parse {
                    let! r = pint32
                    let! _ = clauseEnd
                    return Some r
                }
            | _ -> preturn None

        return
            { columns = columns
              distinct = distinct.IsSome
              from = from
              where = where
              groupBy = groupBy
              having = having
              orderBy = orderBy
              limit = limit
              offset = offset }
    }

let selectQuery select =
    parse {
        do! keyword K.Select
        let! s = headlessSelect select
        return { withAliases = []; select = s }
    }

let rec withSelect =
    let justSelect =
        parse {
            do! keyword K.Select
            return! headlessSelect withSelect
        }

    let queryAs =
        parse {
            let! name = ident
            do! keyword K.As
            let! select = parens justSelect
            return { alias = name; select = select }
        }

    parse {
        do! keyword K.With
        let! xs, _ = sepBy1End queryAs (symbol S.Comma) (keyword K.Select)
        let! s = headlessSelect withSelect
        return { withAliases = xs; select = s }
    }
    <|> parse { return! selectQuery withSelect }
