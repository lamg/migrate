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

module Migrate.FsGeneration.Util

open System.Data
open Microsoft.Data.Sqlite

type ReaderExecuter =
  abstract member ExecuteReader: string -> IDataReader

type SqliteReaderExecuter(connection: SqliteConnection, transaction: SqliteTransaction) =
  interface ReaderExecuter with
    member _.ExecuteReader(sql: string) =
      let command = new SqliteCommand(sql, connection, transaction)
      command.ExecuteReader()
