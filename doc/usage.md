# Usage

## Initialize a new database project

```shell
mkdir new_db
cd new_db
mig init
```

The above commands generate the following files

```
new_db.sqlite3
.env
schema.sql 
db.toml
```

Each file has the following role:
- `db.toml`: main project file
- `.env`: environment variables used in `db.toml` file. It contains the values:
    - `new_db=new_db.sqlite3`: The variable `new_db` is used in `db.toml` to point to the
    SQLite3 database managed by this project.
    - `pull_script=pull_database.sh`: The variable `pull_script` is used in `db.toml` to point
    to the shell script used to bring the SQLite3 database to the expected path, defined in the
    value above.
- `schema.sql`: SQL file used in `db.toml` for specifying the database schema
- `new_db.sqlite3`: SQLite3 database.

## Project status

The following command shows the statements to be executed in case 
a migration is needed to transform the database to the schema specified
in the project.

```
mig status
```

Output:
```
step 0
reason: Added "user"
statement 0 ✅
CREATE TABLE user(id integer NOT NULL,
        name text NOT NULL);
```

## Executing a migration

In the above section after reviewing the proposed changes to the database
schema, we can execute them by running `mig commit`.

After this running `mig status` again will output the following:

```
Nothing to migrate
Latest project version: 0.0.1
Latest database version: 0.0.1
```

## Checking the committed changes

The command `mig log` shows the list of migrations

```
versionRemarks: initial
schema version: 0.0.1
hash: 12427e091c083911637a69dbf609d83628dd8d9851ee028c2029498862d31241
date: 2023-11-07T15:40:03.285+00:00
database: new_db.sqlite3
```

A more detailed summary of the last migration can be showed by `mig log -l`
Show the last commit contents

```
versionRemarks: initial
schema version: 0.0.1
hash: 12427e091c083911637a69dbf609d83628dd8d9851ee028c2029498862d31241
date: 2023-11-07T15:40:03.285+00:00

step 0: Added "user"
statement 0 ✅
CREATE TABLE user(id integer NOT NULL,
        name text NOT NULL);
```

In case the steps are too long you can restrict the details to the reason
behind each step migration with `mig log -s`

```
versionRemarks: initial
schema version: 0.0.1
hash: 12427e091c083911637a69dbf609d83628dd8d9851ee028c2029498862d31241
date: 2023-11-07T15:40:03.285+00:00

✅ step 0: Added "user"
```

# Pulling the latest data from production

The `mig pull` command allows you to download the latest database from a production environment.
In order to work you need to set an environment variable with the path to a shell script which
pulls the database:

```toml file:db.toml
db_file = "schema.sql"

pull_script = new_db_pull_var
```

The shell script could look this way:

```shell file:new_db_pull.sh
rm /path/to/local/new_db.sqlite3
scp user@server_ip:/path/to/new_db.sqlite3 /path/to/local/new_db.sqlite3
```
