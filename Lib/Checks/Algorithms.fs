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

module Migrate.Checks.Algorithms

let topologicalSort reference xs =
  let mutable graph = xs |> List.map (fun x -> (x, reference x)) |> Map.ofList
  let mutable result = []

  while graph.Count > 0 do
    let node =
      graph
      |> Map.filter (fun key _ -> not (graph |> Map.exists (fun _ v -> List.contains key v)))
      |> Map.tryFindKey (fun _ _ -> true)

    match node with
    | Some node ->
      result <- node :: result
      graph <- graph |> Map.remove node
    | None -> failwith "The graph has a cycle"

  result
