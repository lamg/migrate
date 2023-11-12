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

module SqlParser.CreateIndex

open FParsec.Primitives
open Basic
open Types

module K = Keyword
module S = Symbol

let createIndex: Parser<CreateIndex, unit> =
    parse {
        do! keyword K.Index
        let! _ = opt (keyword K.If >>. keyword K.Not >>. keyword K.Exists)
        let! indexName = ident
        do! keyword K.On
        let! tableName = ident
        let! column = ident |> parens
        do! symbol S.Semicolon

        return
            { name = indexName
              table = tableName
              column = column }
    }
