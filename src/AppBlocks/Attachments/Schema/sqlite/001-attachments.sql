-- Files that belong to records, as blobs, and the links that say which record each file belongs to.
-- Idempotent: every start runs it. A file is stored once and never replaced; a removed link keeps its rows
-- until an administrator purges them.
CREATE TABLE IF NOT EXISTS APP_ATTACHMENT (
    ATTACHMENT_ID INTEGER PRIMARY KEY,
    FILE_NAME     TEXT    NOT NULL,
    CONTENT_TYPE  TEXT    NOT NULL,
    SIZE          INTEGER NOT NULL,
    SHA256        TEXT    NOT NULL,
    CONTENT       BLOB    NOT NULL,
    UPLOADED_BY   TEXT    NOT NULL,
    UPLOADED_AT   TEXT    NOT NULL
);
CREATE TABLE IF NOT EXISTS APP_ATTACHMENT_LINK (
    LINK_ID       INTEGER PRIMARY KEY,
    OWNER_ALIAS   TEXT    NOT NULL,
    OWNER_KEY     TEXT    NOT NULL,
    ATTACHMENT_ID INTEGER NOT NULL REFERENCES APP_ATTACHMENT (ATTACHMENT_ID),
    PURPOSE       TEXT    NULL,
    LABEL         TEXT    NULL,
    NOTES         TEXT    NULL,
    CREATED_BY    TEXT    NOT NULL,
    CREATED_AT    TEXT    NOT NULL,
    UPDATED_BY    TEXT    NOT NULL,
    UPDATED_AT    TEXT    NOT NULL,
    REMOVED_BY    TEXT    NULL,
    REMOVED_AT    TEXT    NULL
);
CREATE INDEX IF NOT EXISTS IX_APP_ATTACHMENT_LINK_OWNER ON APP_ATTACHMENT_LINK (OWNER_ALIAS, OWNER_KEY, REMOVED_AT);
