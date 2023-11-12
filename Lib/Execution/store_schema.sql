CREATE TABLE github_com_lamg_migrate_migration
(
    id             integer PRIMARY KEY AUTOINCREMENT,
    hash           TEXT NOT NULL,
    versionRemarks text NOT NULL,
    date           text NOT NULL,
    dbFile         text NOT NULL,
    schemaVersion  text NOT NULL
);

CREATE TABLE github_com_lamg_migrate_step
(
    migrationId integer NOT NULL,
    stepIndex   integer NOT NULL,
    reason      text    NOT NULL,
    sql         text    NOT NULL,
    PRIMARY KEY (migrationId, stepIndex)
);

CREATE TABLE github_com_lamg_migrate_error
(
    migrationId integer NOT NULL,
    stepIndex   integer NOT NULL,
    error       text    NOT NULL,
    PRIMARY KEY (migrationId, stepIndex)
);
