# The project file

The project file is a TOML file that contains the configuration of the project. It is used to specify the database files, the SQL files and the reports. The project file is used by the `mig` command to perform migrations, reports and other operations.

It defines the following fields:
- `db_file`: Environment variable whose value points to a SQLite database 
- `files`: A list of SQL files that define the desired schema in each one of the database files.
- `pull_script`: Name of an environment variable whose value is the path of the script used to pull the database from production.
- `report`: An that specifies the configuration of the reports feature. Several of them can exist.
- `schema_version`: Semantic version of the current database schema. Only when this version is higher than the one stored in the database, the `mig commit` command executes the migration.
- `version_remarks`: A message explaining the particularities of
the current version schema.
- `table_sync`: a list of tables whose values are synchronized
with an insert statement in one of the project files.
- `table_init`: a list of tables whose values will be set from an insert
statement, in case the table is empty.