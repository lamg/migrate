# Database DSL

- Database structure is derived automatically from F# types.
- The mapping from functional types to database tables is in 2NF.
- Types can have attributes that determine which SQL queries are generated.

## Type mapping

| F# type | SQLite type |
|---------|-------------|
| `int64` | `INTEGER NOT NULL` |
| `int64<'u>` | `INTEGER NOT NULL` |
| `string` | `TEXT NOT NULL` |
| `float` | `REAL NOT NULL` |
| `float<'u>` | `REAL NOT NULL` |
| `byte[]` | `BLOB NOT NULL` |
| nullary, non-generic DU | `TEXT NOT NULL` |

All columns are `NOT NULL`. Optional data is represented using discriminated unions and extension tables (see [Optional information](#optional-information)).

Scalar discriminated unions are persisted as the exact F# case name. For example, `InProgress` is stored as `TEXT` value `"InProgress"`.

Units of measure are currently not preserved by compiled-schema reflection/code generation. The underlying SQLite type is still `INTEGER` or `REAL`, but generated APIs currently expose the erased numeric type.

## Translation rules

### Simple record

```fsharp
type Student = { name:string; age: int64 }
```

translates to

```sql
CREATE TABLE student(name TEXT NOT NULL, age INTEGER NOT NULL);
```

### Primary keys

`PK` declares a primary key without autoincrement, suitable for lookup tables with manually assigned IDs:

```fsharp
[<PK "id">]
type AuthPlatform = { id: int64; name: string }
```

translates to

```sql
CREATE TABLE auth_platform(id INTEGER PRIMARY KEY, name TEXT NOT NULL);
```

Text primary keys are supported:

```fsharp
[<PK "id">]
type Session = { id: string; userId: int64; expiresAt: string }
```

translates to

```sql
CREATE TABLE session(id TEXT PRIMARY KEY, user_id INTEGER NOT NULL, expires_at TEXT NOT NULL);
```

`AutoIncPK` declares an autoincrement primary key:

```fsharp
[<AutoIncPK "id">]
type Student = {id: int64; name: string; age: int64}
```

translates to

```sql
CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, age INTEGER NOT NULL);
```

### Unique constraints

```fsharp
[<AutoIncPK "id">]
[<Unique "email">]
type Student = { id: int64; name: string; email: string }
```

translates to

```sql
CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, email TEXT NOT NULL UNIQUE);
```

Composite unique constraints span multiple columns:

```fsharp
[<AutoIncPK "id">]
[<Unique ("name", "age")>]
type Student = { id: int64; name: string; age: int64 }
```

translates to

```sql
CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, age INTEGER NOT NULL, UNIQUE(name, age));
```

### Default values

```fsharp
[<AutoIncPK "id">]
[<Default ("active", "1")>]
type User = { id: int64; name: string; active: int64 }
```

translates to

```sql
CREATE TABLE user(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, active INTEGER NOT NULL DEFAULT 1);
```

Default expressions (e.g. for timestamps) are specified with `DefaultExpr`:

```fsharp
[<AutoIncPK "id">]
[<DefaultExpr ("createdAt", "strftime('%Y-%m-%dT%H:%M:%SZ', 'now', 'utc')")>]
type Payment = { id: int64; userId: int64; amount: float; createdAt: string }
```

translates to

```sql
CREATE TABLE payment(id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL, amount REAL NOT NULL, created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now', 'utc')));
```

### Explicit source-column drops

Source columns are not dropped implicitly during migration planning.

If a target table intentionally removes a source column, declare it explicitly on the target type:

```fsharp
[<AutoIncPK "id">]
[<DropColumn "legacyNote">]
type Student = { id: int64; name: string }
```

This allows MigLib to accept dropping `legacy_note` from the source table during migration planning.

If `DropColumn` is missing, the planner fails rather than silently dropping data.

`DropColumn` only covers columns from a table that still exists in the target schema. Removing whole source tables remains unsupported.

### Enum-like scalar unions

Nullary, non-generic discriminated unions can be used as scalar columns:

```fsharp
type Status =
  | Pending
  | Running
  | Done
  | Error

[<AutoIncPK "id">]
type Job =
  { id: int64
    status: Status
    externalJobId: string }
```

translates to

```sql
CREATE TABLE job(id INTEGER PRIMARY KEY AUTOINCREMENT, status TEXT NOT NULL, external_job_id TEXT NOT NULL);
```

`mig codegen` re-emits the union definition and generates typed APIs using `Status` rather than `string`. Equality-based generated queries accept the DU type; `SelectLike` remains string-based.

Payload-bearing unions and generic unions are not valid scalar columns.

### Units of measure

Numeric units of measure in `Schema.fs` are stored using their underlying SQLite type:

```fsharp
[<Measure>]
type Byte

[<AutoIncPK "id">]
type File =
  { id: int64
    contentLength: int64<Byte>
    progress: int64<Byte>
    ratio: float<Byte> }
```

translates to

```sql
CREATE TABLE file(id INTEGER PRIMARY KEY AUTOINCREMENT, content_length INTEGER NOT NULL, progress INTEGER NOT NULL, ratio REAL NOT NULL);
```

Current limitation: compiled-schema code generation does not preserve the `[<Measure>]` declaration or the measured F# types in generated records and query signatures.

### Indexes

```fsharp
[<AutoIncPK "id">]
[<Index "name">]
[<Index ("name", "age")>]
type Student = { id: int64; name: string; age: int64 }
```

translates to

```sql
CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, age INTEGER NOT NULL);

CREATE INDEX ix_student_name ON student(name);
CREATE INDEX ix_student_name_age ON student(name, age);
```

### Optional information

Each DU case maps to exactly one extension table. The fields beyond the base record become the columns of that table. Only the cases you need are defined — there is no requirement to enumerate all combinations of optional fields.

```fsharp
[<AutoIncPK "id">]
type Student = {id: int64; name: string; age: int64}

type StudentOpt =
| WithEmail of Student * email:string
| WithAddress of Student * address: string
| WithEmailAddress of Student * email: string * address:string
```

translates to

```sql
CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, age INTEGER NOT NULL);

CREATE TABLE student_email(student_id INTEGER NOT NULL, email TEXT NOT NULL, FOREIGN KEY (student_id) REFERENCES student(id));

CREATE TABLE student_address(student_id INTEGER NOT NULL, address TEXT NOT NULL, FOREIGN KEY (student_id) REFERENCES student(id));

CREATE TABLE student_email_address(student_id INTEGER NOT NULL, email TEXT NOT NULL, address TEXT NOT NULL, FOREIGN KEY (student_id) REFERENCES student(id));
```

Alternatively, optional aspects can be expressed as separate records referencing the base type. Each record generates one extension table:

```fsharp
[<AutoIncPK "id">]
type Student = { id: int64; name: string; age: int64 }

type StudentEmail = { student: Student; email: string }
type StudentAddress = { student: Student; address: string }
```

translates to

```sql
CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, age INTEGER NOT NULL);

CREATE TABLE student_email(student_id INTEGER NOT NULL, email TEXT NOT NULL, FOREIGN KEY (student_id) REFERENCES student(id));

CREATE TABLE student_address(student_id INTEGER NOT NULL, address TEXT NOT NULL, FOREIGN KEY (student_id) REFERENCES student(id));
```

### Queries

An `Insert` method is always generated for every type. Additional query methods are generated by adding their corresponding attributes:

```fsharp
[<AutoIncPK "id">]
[<SelectAll>]
[<UpdateBy "id">]
[<DeleteBy "id">]
[<SelectBy ("name", "age")>]
[<SelectBy ("name", "age", OrderBy = "name DESC")>]
[<SelectBy ("name", OrderBy = "name DESC, age ASC")>]
[<SelectLike "name">]
[<SelectOneBy ("name", "age")>]
[<SelectOneBy ("name", "age", OrderBy = "name DESC")>]
[<SelectByOrInsert ("name", "age")>]
[<InsertOrIgnore>]
[<Upsert>]
type Student = {id: int64; name: string; age: int64}
```

The `SelectBy`, `SelectOneBy`, and `SelectAll` attributes accept an optional `OrderBy` named parameter. The value is a comma-separated list of columns, each optionally followed by `ASC` (default) or `DESC`.

```fsharp
type Student with
  static member Insert (student: Student) (txn: SqliteTransaction): Task<Result<int64, SqliteException>> =
    // INSERT INTO student (name, age) VALUES (@name, @age)
    // returns last_insert_rowid()

  static member SelectAll () (txn: SqliteTransaction): Task<Result<Student list, SqliteException>> =
    // SELECT * FROM student

  static member UpdateById (student: Student) (txn: SqliteTransaction): Task<Result<unit, SqliteException>> =
    // UPDATE student SET name = @name, age = @age WHERE id = @id

  static member DeleteById (id: int64) (txn: SqliteTransaction): Task<Result<unit, SqliteException>> =
    // DELETE FROM student WHERE id = @id

  static member SelectByNameAge (name: string, age: int64) (txn: SqliteTransaction): Task<Result<Student list, SqliteException>> =
    // SELECT * FROM student WHERE name = @name AND age = @age

  static member SelectByNameAgeOrderByNameDesc (name: string, age: int64) (txn: SqliteTransaction): Task<Result<Student list, SqliteException>> =
    // SELECT * FROM student WHERE name = @name AND age = @age ORDER BY name DESC

  static member SelectByNameOrderByNameDescAgeAsc (name: string) (txn: SqliteTransaction): Task<Result<Student list, SqliteException>> =
    // SELECT * FROM student WHERE name = @name ORDER BY name DESC, age ASC

  static member SelectNameLike (name: string) (txn: SqliteTransaction): Task<Result<Student list, SqliteException>> =
    // SELECT * FROM student WHERE name LIKE '%' || @name || '%'

  static member SelectOneByNameAge (name: string, age: int64) (txn: SqliteTransaction): Task<Result<Student option, SqliteException>> =
    // SELECT * FROM student WHERE name = @name AND age = @age LIMIT 1

  static member SelectOneByNameAgeOrderByNameDesc (name: string, age: int64) (txn: SqliteTransaction): Task<Result<Student option, SqliteException>> =
    // SELECT * FROM student WHERE name = @name AND age = @age ORDER BY name DESC LIMIT 1

  static member SelectByNameAgeOrInsert (student: Student) (txn: SqliteTransaction): Task<Result<Student, SqliteException>> =
    // searches by name and age; if not found, inserts and returns the new record

  static member InsertOrIgnore (student: Student) (txn: SqliteTransaction): Task<Result<int64 option, SqliteException>> =
    // INSERT OR IGNORE INTO student (name, age) VALUES (@name, @age)
    // returns Some last_insert_rowid() if inserted, None if ignored

  static member Upsert (student: Student) (txn: SqliteTransaction): Task<Result<unit, SqliteException>> =
    // checks by primary key; updates when found, inserts when missing
```

`Upsert` is only valid on tables that have a primary key. Code generation fails when `Upsert` is used on a table without a primary key or on a view.

### dbTxn CE

The `dbTxn` computation expression allows you to execute asynchronous queries inside a transaction given a database path:

```fsharp
dbTxn "path_to_db.sqlite" {
  let! students = Student.SelectAll()
  let! phil = Student.SelectByName "Phil"
  return students, phil
}
```

The database path should be an explicit SQLite file path.

When using `dbTxn` it is a good idea to make it part of the environment passed to functions so they have access to the database:

```fsharp
type Env = { dbTxn: DbTxnBuilder }
let env = { dbTxn = dbTxn "path_to_db.sqlite" }

let printStudents (env: Env) =
  env.dbTxn {
    let! students = Student.SelectAll
    for student in students do
      printfn $"student: {student}"
  }
```

Within `dbTxn` and `txn`, `let!` can bind generated query steps, plain `Task<'a>` values, and `Task<Result<'a, _>>` values. Non-`SqliteException` errors from `Task<Result<_, _>>` bindings are converted into `SqliteException`.

### txn CE

The `txn` computation expression builds reusable transaction-scoped operations. It does not open a connection or start/commit/rollback a transaction by itself. Its purpose is to compose helper functions that can be executed inside a parent `dbTxn` transaction.

```fsharp
let insertStudent (name: string) =
  txn {
    // id = 0 is ignored because in the above Student definition is marked
    // as autoincrement
    let student = { id = 0L; name = name; age = 18L }
    let! (_actualId:int64) = Student.Insert student
    return ()
  }

dbTxn "path_to_db.sqlite" {
  do! insertStudent "Alice"
  do! insertStudent "Bob"
}
```

Use `dbTxn` when you need the transaction boundary. Use `txn` when you need a composable operation that runs inside an existing transaction.

### Foreign keys

Foreign keys are automatically generated when a record field references another type defined in the same schema module. The referenced type must have a primary key.

```fsharp
[<AutoIncPK "id">]
type Student = { id: int64; name: string; age: int64 }

[<AutoIncPK "id">]
type Course = { id: int64; title: string; student: Student }
```

translates to

```sql
CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, age INTEGER NOT NULL);

CREATE TABLE course(id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, student_id INTEGER NOT NULL, FOREIGN KEY (student_id) REFERENCES student(id));
```

FK actions are specified with `OnDeleteCascade` or `OnDeleteSetNull`:

```fsharp
[<AutoIncPK "id">]
type User = { id: int64; name: string }

[<AutoIncPK "id">]
[<OnDeleteCascade "user">]
type UserWallet = { id: int64; address: string; user: User }
```

translates to

```sql
CREATE TABLE user(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);

CREATE TABLE user_wallet(id INTEGER PRIMARY KEY AUTOINCREMENT, address TEXT NOT NULL, user_id INTEGER NOT NULL, FOREIGN KEY (user_id) REFERENCES user(id) ON DELETE CASCADE);
```

### Views

Views are declared as record types with `Join` attributes specifying how tables are connected. The join conditions are inferred from foreign key relationships between the referenced types. Selected columns are determined by the fields of the view record.

```fsharp
[<AutoIncPK "id">]
type Student = { id: int64; name: string; age: int64 }

[<AutoIncPK "id">]
type Course = { id: int64; title: string; student: Student }

type CourseGrade = { course: Course; grade: float }

[<View>]
[<Join(typeof<Course>, typeof<Student>)>]
[<Join(typeof<CourseGrade>, typeof<Course>)>]
[<SelectBy "studentId">]
type StudentCourseGrade = {
  studentId: int64
  studentName: string
  title: string
  grade: float
}
```

translates to

```sql
CREATE VIEW student_course_grade AS
SELECT s.id AS student_id, s.name AS student_name, c.title, cg.grade
FROM course c
JOIN student s ON c.student_id = s.id
JOIN course_grade cg ON cg.course_id = c.id;
```

For views that require expressions beyond simple joins (aggregations, subqueries, CASE expressions, etc.), use `ViewSql` with the raw SELECT statement:

```fsharp
[<ViewSql "SELECT c.id AS course_id, c.title, COUNT(s.id) AS student_count, AVG(s.age) AS avg_age
  FROM course c
  JOIN student s ON c.student_id = s.id
  GROUP BY c.id, c.title">]
[<SelectAll>]
type CourseStats = {
  courseId: int64
  title: string
  studentCount: int64
  avgAge: float
}
```

Query attributes (`SelectBy`, `SelectLike`, etc.) work on views the same way as on tables.

### Seed data

Module-level `let` bindings of record types or lists of record types are translated as `INSERT OR REPLACE` statements. The values are inserted in dependency order respecting foreign keys.

```fsharp
[<AutoIncPK "id">]
type Role = { id: int64; name: string }

[<AutoIncPK "id">]
type Student = { id: int64; name: string; age: int64 }

let roles = [
  { id = 1L; name = "admin" }
  { id = 2L; name = "teacher" }
  { id = 3L; name = "student" }
]

let defaultStudent = { id = 1L; name = "System"; age = 0L }
```

translates to

```sql
INSERT OR REPLACE INTO role(id, name) VALUES (1, 'admin');
INSERT OR REPLACE INTO role(id, name) VALUES (2, 'teacher');
INSERT OR REPLACE INTO role(id, name) VALUES (3, 'student');

INSERT OR REPLACE INTO student(id, name, age) VALUES (1, 'System', 0);
```

## SQL generation

The supported CLI code-generation path now starts from compiled schema types plus `Schema.fs` as the source file for schema-bound naming.

Use `mig codegen` to materialize generated `Db.fs` code next to `Schema.fs`:

```sh
mig codegen [--dir|-d /path/to/project] [--assembly|-a /path/to/App.dll] [--schema-module|-s Schema] [--module|-m Db] [--output|-o Db.fs]
```

The CLI reads `Schema.fs` to derive the schema-bound SQLite filename, reflects tables/views/query helpers from the compiled schema module, emits a `DbFile` literal whose value is `<dir-name>-<schema-hash>.sqlite`, and writes the output file into the same directory as `Schema.fs`. The default generated module is `Db`, and the default output file is `Db.fs`. The output file must be a plain file name, not an absolute path or subdirectory path.

Generated source preserves:

- nullary, non-generic scalar DUs as typed F# fields and query parameters, while storing them as strings in SQLite
