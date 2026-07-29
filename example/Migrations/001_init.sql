-- mig:rel Student
-- mig:ops insert, select_by_id, select_one_by(name), select_all, upsert, delete_by_id
CREATE TABLE student (
  id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  age INTEGER NOT NULL
) STRICT;
