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

module SqlParser.Keyword

type Keyword =
    | Abort
    | Action
    | Add
    | After
    | All
    | Alter
    | Analyze
    | And
    | As
    | Asc
    | Attach
    | Autoincrement
    | Before
    | Begin
    | Between
    | By
    | Cascade
    | Case
    | Cast
    | Check
    | Collate
    | Column
    | Commit
    | Conflict
    | Create
    | Database
    | Default
    | Deferrable
    | Deferred
    | Delete
    | Desc
    | Detach
    | Distinct
    | Each
    | Else
    | End
    | Escape
    | Except
    | Exclusive
    | Exists
    | Extract
    | False
    | For
    | Foreign
    | From
    | Full
    | Glob
    | Group
    | Having
    | If
    | Immediate
    | In
    | Index
    | Initially
    | Inner
    | Insert
    | Integer
    | Into
    | Is
    | Join
    | Key
    | Last
    | Left
    | Like
    | Limit
    | Match
    | Natural
    | Not
    | Null
    | Offset
    | On
    | Or
    | Order
    | Outer
    | Over
    | Partition
    | Pragma
    | Primary
    | References
    | Regexp
    | Reindex
    | Release
    | Replace
    | Restrict
    | Right
    | Rollback
    | Rowid
    | Select
    | Set
    | Table
    | Temporary
    | Text
    | Then
    | To
    | Transaction
    | Trigger
    | Union
    | Unique
    | Update
    | Using
    | Vacuum
    | Values
    | View
    | When
    | Where
    | With
