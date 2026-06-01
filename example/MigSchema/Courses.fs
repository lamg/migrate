[<MigLib.Dsl.Attributes.GeneratedDbNamespace("ExampleApp")>]
module ExampleMigSchema.Courses

open MigLib.Dsl.Attributes

[<AutoIncPK "id">]
[<Unique "code">]
[<SelectBy "code">]
type Course =
  {
    id: int64
    code: string
    title: string
  }

[<AutoIncPK "id">]
[<SelectBy "student_id">]
[<SelectBy "course_id">]
type Enrollment =
  {
    id: int64
    student: MigSchema.Student
    course: Course
    enrolledOn: string
  }

let introCourse: Course =
  {
    id = 0L
    code = "intro-fsharp"
    title = "Intro to F#"
  }
