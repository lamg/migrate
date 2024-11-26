# Declarative migrations

Given two SQL database schemas, `actual` and `expected`, in most cases the steps to transform `actual` into
`expected` can be calculated using simple rules:

- When an element (table, view, index, trigger) appears on `actual` and not on `expected` it should be dropped.
- When an element appears on `expected` and not on `actual` it should be created according the definition in `expected`
- When an element has the same structure in `actual` and `expected` and the name on `actual` does not appear on
  `expected` then it should be renamed.

Since there are dependencies of the following kind:

- table to table (through foreign keys)
- views to views or tables
- index to tables
- triggers to views or tables

Then the generated migration needs steps in an order where statements modify existing definitions. To achieve that dependencies between the above elements are found and topologically sorted before generating the SQL statements.

## Column migrations

Columns without constraints are handled by finding the differences between columns matching tables (by name) and generating statements like:

```sqlite
ALTER TABLE student ADD COLUMN name TEXT NOT NULL DEFAULT '';
ALTER TABLE student DROP COLUMN name;
```

Columns with constraints require a more careful analysis:

Actual:
```sqlite
CREATE TABLE student (id integer PRIMARY KEY);
```

Expected:

```sqlite
CREATE TABLE teacher (id integer primary key);

CREATE TABLE student (
  id integer PRIMARY KEY,
  teacher_id integer NOT NULL,
  FOREIGN KEY (teacher_id) REFERENCES teacher (id)
);
```

The generated migration would be:

```sqlite
-- WARNING addition of columns [teacher_id integer NOT NULL] requires a complimentary script to ensure data integrity;
CREATE TABLE teacher (id integer primary key);

ALTER TABLE student RENAME TO student_old;

CREATE TABLE student (
  id integer PRIMARY KEY,
  teacher_id integer NOT NULL,
  FOREIGN KEY (teacher_id) REFERENCES teacher (id)
);
```

When adding columns to a table, the data they hold requires a more complex migration that currently this program cannot handle, thus the user has to write a complimentary script.

Now let's analyze how we would go from the above schema with `teacher` and `student` to the original with just `student`:

```sqlite
CREATE TABLE student_temp (id integer PRIMARY KEY);
INSERT INTO student_temp (id) SELECT id FROM student;
DROP TABLE student;
ALTER TABLE student_temp RENAME TO student;
DROP TABLE teacher;
```