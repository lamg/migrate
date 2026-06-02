module Test.Codegen.ViewDependenciesTests

open MigLib.Codegen
open MigLib.Schema.Types
open Xunit

let private view name sql dependencies =
  { name = name
    previousName = None
    sql = sql
    declaredColumns = []
    dependencies = dependencies
    queryByAnnotations = []
    queryLikeAnnotations = []
    queryWhereAnnotations = []
    queryByOrCreateAnnotations = []
    selectOneAnnotations = []
    selectOneByAnnotations = []
    insertOrIgnoreAnnotations = []
    deleteWhereAnnotations = []
    deleteAllAnnotations = []
    upsertAnnotations = [] }

[<Fact>]
let ``orderViews infers SQL dependencies and sorts dependent views`` () =
  let baseTable =
    { name = "student"
      previousName = None
      dropColumns = []
      columns = []
      constraints = []
      queryByAnnotations = []
      queryLikeAnnotations = []
      queryWhereAnnotations = []
      queryByOrCreateAnnotations = []
      selectOneAnnotations = []
      selectOneByAnnotations = []
      insertOrIgnoreAnnotations = []
      deleteWhereAnnotations = []
      deleteAllAnnotations = []
      upsertAnnotations = [] }

  let dependentView =
    view "student18_a" "SELECT id FROM student18 WHERE name LIKE 'A%'" []

  let baseView = view "student18" "SELECT id FROM student WHERE age >= 18" []

  match ViewDependencies.orderViews [ baseTable ] [ dependentView; baseView ] with
  | Ok views ->
    Assert.Equal<string list>([ "student18"; "student18_a" ], views |> List.map _.name)
    Assert.Equal<string list>([ "student" ], views[0].dependencies)
    Assert.Equal<string list>([ "student18" ], views[1].dependencies)
  | Error error -> failwith $"Expected views to sort, got: {error}"

[<Fact>]
let ``orderViews reports dependency cycles`` () =
  let left = view "left_view" "SELECT id FROM right_view" []
  let right = view "right_view" "SELECT id FROM left_view" []

  match ViewDependencies.orderViews [] [ left; right ] with
  | Ok views -> failwith $"Expected cycle error, got: {views |> List.map _.name}"
  | Error error -> Assert.Contains("View dependency cycle detected", error)
