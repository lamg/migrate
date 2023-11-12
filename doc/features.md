# Features

## Synchronized tables

Synchronized tables are tables that fetch values directly from the source
code. They are particularly useful for storing configurations in databases,
allowing us to manipulate configuration values just like any other data in
the database.

With the following project definition:

```toml file:db.toml
version_remarks = "project initialization"
schema_version = "0.0.1
db_file = "database_path_env_var"
files = ["profile.sql"]
sync = ["watched_profile"]

```

```sql file:profile.sql
CREATE TABLE profile(id text NOT NULL);

CREATE TABLE watched_profile(profile_id text PRIMARY KEY, created_at text NOT NULL);

INSERT INTO watched_profiles(profile_id, created_at)
VALUES ('101010', '2023'), ('11111', '2022');
```

and database schema with the same tables but the synchronized table `watched_profile`
has the following values

| profile_id | created_at |
| ---------- | ---------- |
| '101010'   | '2022'     |
| '0'        | '2021'     |

the generated migration is

```sql
UPDATE watched_profile SET created_at = '2023' WHERE profile_id = '101010';
DELETE FROM watched_profile WHERE profile_id = '0';
INSERT INTO watched_profile(profile_id, created_at) VALUES ('11111', '2022');
```

## Reports

Sometimes we have a view whose computation takes long but produces
the same result if made inside a reasonably long interval of time.
The usual solution for that problem is to create a cache which allows
to retrieve results quickly. Having that in mind the reports feature
makes easy to update those caches, by specifying a view and the corresponding
table that is going to be used as cache.

Let's take a look to the following example project

```toml file:db.toml
version_remarks = "project initialization"
schema_version = "0.0.1"
db_file = "tweets_db.sqlite3"
files = ["profile.sql"]

[[report]]
src = "report0_view"
dest = "report0"
```

```sql file:profile.sql
CREATE VIEW report0_view SELECT 1, '2020';

CREATE TABLE report(id integer NOT NULL, date text NOT NULL);
```

In this context you can run the following command:

```sh
mig report -s
```

and you will get the values from `report0_view` into `report`.
Also comes hand a command for showing those values in the terminal:

```sh
mig report -o
```

which shows the following output

```
id| date
--------
1|2020
```

## Schema versions

The database has a schema version, starting at `0.0.0` by default and the project has 
also a schema version in `schema_version`. The migration only runs when the project has a schema
version higher than the one stored in the database.

## Manual migration

In some cases the automatically generated migration is not adequate, so there's the option
to introduce manually the SQL code to be executed. The result of the code execution must be
that in the end both the database and project schemas are the same, otherwise the manual migration
fails.

Executing the command `mig commit -m` looks the following way on 
the terminal (the SQL code is introduced by the user):

```
please write the SQL code for the migration and press Ctrl+D
      
CREATE TABLE user(id integer NOT NULL,
        name text NOT NULL);

executing migration…
```

## Summary of relations

The tool can show the type signatures of relations.

- `mig relations -db` shows relation type signatures in the DB
- `mig relations -p` shows relation type signatures in project source
