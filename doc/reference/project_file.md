# The project file

The project file is a TOML file that contains the configuration of the project. It is used to specify the database files, the SQL files and the reports. The project file is used by the `mig` command to perform migrations, reports and other operations.

It defines the following fields:
- `db_file`: Environment variable whose value points to a SQLite database 
- `files`: A list of SQL files that define the desired schema in each one of the database files.
- `pull_script`: Name of an environment variable whose value is the path of the script used to pull the database from production.
- `report`: An that specifies the configuration of the reports feature. Several of them can exist.
