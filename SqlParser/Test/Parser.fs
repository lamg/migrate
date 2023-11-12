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

module Parser

open Xunit
open SqlParser.Types
open Util

let text0 =
    "
CREATE TABLE table0(
  id integer NOT NULL,
  name text NOT NULL
);

-- a comment
CREATE TABLE table1(
  id text NOT NULL,
  UNIQUE (id)
);

-- original tweets (i.e. not retweeted or cited)
CREATE TABLE table2(
  id text PRIMARY KEY
);

-- indexes
CREATE INDEX IF NOT EXISTS table0_id ON table0(id);

"

[<Fact>]
let parseText0 () =
    let expectedTables =
        [ { name = "table0"
            columns =
              [ { name = "id"
                  ``type`` = SqlInteger
                  constraints = [ NotNull ] }
                { name = "name"
                  ``type`` = SqlText
                  constraints = [ NotNull ] } ]

            constraints = [] }
          { name = "table1"
            columns =
              [ { name = "id"
                  ``type`` = SqlText
                  constraints = [ NotNull ] } ]

            constraints = [ Unique [ "id" ] ] }
          { name = "table2"
            columns =
              [ { name = "id"
                  ``type`` = SqlText
                  constraints = [ PrimaryKey None ] } ]
            constraints = [] } ]

    let expectedIndexes =
        [ { name = "table0_id"
            table = "table0"
            column = "id" } ]

    let r = SqlParser.Parser.parseSql "text0" text0

    match r with
    | Ok f ->
        Assert.Equal(3, f.tables.Length)
        Assert.Equal(0, f.views.Length)
        Assert.Equal(0, f.inserts.Length)
        Assert.Equal(1, f.indexes.Length)

        Assert.Equal<List<CreateTable>>(expectedTables, f.tables)
        Assert.Equal<List<CreateIndex>>(expectedIndexes, f.indexes)
    | Error e -> Assert.Fail e

let text1 =
    "
-- view for creating a batch of the next profiles to be updated
CREATE VIEW view0 AS
SELECT id AS x
FROM t0
LIMIT 100;

CREATE VIEW view1 AS
SELECT id
FROM t1
WHERE
  id NOT IN (SELECT id FROM t2)
  AND id NOT IN (SELECT id FROM t3);
"

[<Fact>]
let parseText1 () =
    // empty select statement
    let empty =
        { distinct = false
          columns = []
          from = []
          where = None
          groupBy = []
          having = None
          orderBy = None
          limit = None
          offset = None }

    let r = SqlParser.Parser.parseSql "text1" text1

    match r with
    | Ok f ->
        Assert.Equal(0, f.tables.Length)
        Assert.Equal(2, f.views.Length)
        Assert.Equal(0, f.inserts.Length)
        Assert.Equal(0, f.indexes.Length)

        let view0 =
            { name = "view0"
              select =
                { withAliases = []
                  select =
                    { empty with
                        columns = [ Alias { expr = col "id"; alias = "x" } ]
                        from = [ table "t0" ]
                        limit = Some 100 } } }

        let view1 =
            { name = "view1"
              select =
                { withAliases = []
                  select =
                    { empty with
                        columns = [ col "id" ]
                        from = [ table "t1" ]
                        where =
                            Some(
                                sand
                                    (Not(
                                        In
                                            { left = col "id"
                                              right =
                                                SubQuery
                                                    { withAliases = []
                                                      select =
                                                        { empty with
                                                            columns = [ col "id" ]
                                                            from = [ table "t2" ] } } }
                                    ))
                                    (Not(
                                        In
                                            { left = col "id"
                                              right =
                                                SubQuery
                                                    { withAliases = []
                                                      select =
                                                        { empty with
                                                            columns = [ col "id" ]
                                                            from = [ table "t3" ] } } }
                                    ))
                            ) } } }

        Assert.Equal<CreateView list>([ view0; view1 ], f.views)
    | Error e -> Assert.Fail e
