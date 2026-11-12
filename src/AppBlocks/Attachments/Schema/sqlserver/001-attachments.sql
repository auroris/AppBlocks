-- Files that belong to records, as blobs, and the links that say which record each file belongs to.
-- Idempotent: every start runs it. A file is stored once and never replaced; a removed link keeps its rows
-- until an administrator purges them. The names are unqualified, as the store's queries are: an account that creates these
-- gets them in its default schema, and SQL Server looks in dbo next, which is where an owner running this by hand puts them.
IF OBJECT_ID(N'APP_ATTACHMENT', N'U') IS NULL
CREATE TABLE APP_ATTACHMENT (
    ATTACHMENT_ID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_APP_ATTACHMENT PRIMARY KEY,
    FILE_NAME     nvarchar(260)  NOT NULL,
    CONTENT_TYPE  nvarchar(200)  NOT NULL,
    SIZE          bigint         NOT NULL,
    SHA256        char(64)       NOT NULL,
    CONTENT       varbinary(max) NOT NULL,
    UPLOADED_BY   nvarchar(200)  NOT NULL,
    UPLOADED_AT   datetime2      NOT NULL
);
GO
IF OBJECT_ID(N'APP_ATTACHMENT_LINK', N'U') IS NULL
CREATE TABLE APP_ATTACHMENT_LINK (
    LINK_ID       bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_APP_ATTACHMENT_LINK PRIMARY KEY,
    OWNER_ALIAS   nvarchar(200)  NOT NULL,
    OWNER_KEY     nvarchar(200)  NOT NULL,
    ATTACHMENT_ID bigint         NOT NULL CONSTRAINT FK_APP_ATTACHMENT_LINK_FILE REFERENCES APP_ATTACHMENT (ATTACHMENT_ID),
    PURPOSE       nvarchar(200)  NULL,
    LABEL         nvarchar(200)  NULL,
    NOTES         nvarchar(1000) NULL,
    CREATED_BY    nvarchar(200)  NOT NULL,
    CREATED_AT    datetime2      NOT NULL,
    UPDATED_BY    nvarchar(200)  NOT NULL,
    UPDATED_AT    datetime2      NOT NULL,
    REMOVED_BY    nvarchar(200)  NULL,
    REMOVED_AT    datetime2      NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_APP_ATTACHMENT_LINK_OWNER' AND object_id = OBJECT_ID(N'APP_ATTACHMENT_LINK'))
CREATE INDEX IX_APP_ATTACHMENT_LINK_OWNER ON APP_ATTACHMENT_LINK (OWNER_ALIAS, OWNER_KEY, REMOVED_AT);
GO
