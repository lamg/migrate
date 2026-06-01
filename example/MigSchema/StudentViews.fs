[<MigLib.Dsl.Attributes.GeneratedDbNamespace("ExampleApp")>]
module ExampleMigSchema.StudentViews

open MigLib.Dsl.Attributes

[<ViewSql "
SELECT id, name, age FROM student
WHERE age >= 18
">]
type Student18 = { id: int64; name: string; age: int64 }

[<ViewSql "
SELECT id, name, age FROM student18
WHERE name like 'A%'
">]
type Student18A = { id: int64; name: string; age: int64 }

[<ViewSql "
SELECT
  enrollment.id,
  student.name AS student_name,
  course.code AS course_code
FROM enrollment
JOIN student ON student.id = enrollment.student_id
JOIN course ON course.id = enrollment.course_id
">]
type EnrollmentSummary =
  {
    id: int64
    studentName: string
    courseCode: string
  }
