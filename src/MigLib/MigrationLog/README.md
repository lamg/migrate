# Migration log

This feature allows storing a migration log in the target database. By default, the subcommands that create migrations like `exec` and `import` introduce the following statements in the migration

```sql
CREATE TABLE migration_log(
    created_at string PRIMARY KEY,
    message string NOT NULL DEFAULT '');

CREATE TABLE migration_step(
    log_created_at string NOT NULL,                         
    step string NOT NULL,                                             
    FOREIGN KEY(log_created_at) REFERENCES migration_log(created_at));

INSERT INTO migration_log(message, created_at) VALUES ('<MESSAGE>', '<RFC3339 DATE>');
INSERT INTO migration_step(log_created_at, step) 
VALUES 
    ('<RFC3339 DATE>', '<SQL STEP 0>'),
    ('<RFC3339 DATE>', '<SQL STEP 1>');
--  …
```

This behavior can be disabled passing the `-nl` global flag, like in the following commands:

```sh
mig -nl import -d <DIRECTORY> -e
# or
mig -nl exec
```

The `log` subcommand allows to see the migration log and detailed steps of a specific migration:

```sh
mig log
```

Output:

```
date: 2024-11-26T09:44:38.331+00:00
message: import Goose migration

```

```sh
mig log -s 2024-11-26T09:44:38.331+00:00
```

Output:

```
CREATE TABLE student(id integer PRIMARY KEY);
…
```