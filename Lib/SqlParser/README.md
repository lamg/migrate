# Migrate.SqlParser

- SQL parser for https://github.com/lamg/migrate
- Support for Sqlite syntax
- Insert statements allow variables in within literal rows. The purpose is getting strings from environment variables

## `INSERT INTO` example

Before executing the following statement the executor must get the value of `account_api_key` from the environment and substitute it where that identifier appears.

```sqlite
INSERT INTO table0(account, api_key)
VALUES ('account0', account0_api_key);
```