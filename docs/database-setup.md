# Database setup

Forms and reports read the site's own tables and need nothing created. Two features keep tables of their own, and create them on first use when the database account may. **Settings → AppBlocks → Database setup** shows administrators each feature's connection, whether its tables are there, and both ways to set it up for that database type. Looking at the screen creates nothing, and it never shows a connection string.

| Feature | Tables | Connection | Without them |
| --- | --- | --- | --- |
| File uploads | `APP_ATTACHMENT`, `APP_ATTACHMENT_LINK` | `Attachments:Connection`, by default `AppBlocksDb` | Attachments blocks show a plain notice instead of files, and nothing can be uploaded. The form around them keeps working. |
| Saved report views (`AppBlocks.InteractiveReports`) | `IR_SAVED_REPORTS`, and `IR_ADMINISTRATORS` for the saved report administration page | `InteractiveReport:SavedReports`, by default `AppBlocksDb` | Saving views is hidden and reports open on their own starting view. Reports still load, query and export. |

Everything else keeps working in that state, and no visitor sees an error. An administrator who is signed in to the backoffice also gets a link to the setup screen. The reason is logged once as a warning, with what to do, rather than on every request.

There are two ways to set a feature up, and either takes effect on the next request, with no restart:

1. **Grant the permission**, so AppBlocks creates the tables itself. The screen prints the statements with the account's name in them.
   - **SQLite** has no accounts: the account the site runs under needs write access to the database file and its folder.
   - **SQL Server**: `CREATE TABLE`, plus `ALTER`, `SELECT`, `INSERT`, `UPDATE`, `DELETE` and `REFERENCES` on the account's default schema, which is where its tables land. That is `dbo` unless the database user says otherwise, and the screen names it. Tables in a schema belong to the schema's owner, not to the account that creates them, so this covers every table in that schema. To limit the account to these tables, use the script.
   - **PostgreSQL**: `USAGE` and `CREATE` on the account's current schema, which the screen names. The account owns what it creates.
   - **Oracle**: `CREATE TABLE` and `CREATE SEQUENCE`, plus `CREATE TRIGGER` on 11.2, and a quota on the account's tablespace. The account owns what it creates.
2. **Run the script**, viewable and downloadable on the screen, as an account that may create tables. The account the site signs in as then needs no right to create anything. The script is made from what AppBlocks itself runs, so the two cannot differ, and it is safe to run more than once. On SQL Server and PostgreSQL it ends by granting that account read and write on those tables. On SQL Server, run as the database's owner, the tables go in `dbo`, which SQL Server looks in after an account's default schema, so an account with a schema of its own still finds them. On PostgreSQL, an owner first sets `search_path` to the account's schema, as the script's header says. On Oracle, connect as the account, or as a DBA after `ALTER SESSION SET CURRENT_SCHEMA = <account>`, so that the tables are in its schema.

File uploads run on SQLite and SQL Server only. On PostgreSQL or Oracle the block shows the same notice and nothing is created: select a separate store with `Attachments:Connection`, as described under [Connections](connections.md).

`APP_SCHEMA` is a bookkeeping table AppBlocks' schema code writes when it creates tables. Nothing reads it back, so uploads neither need it nor wait for it, and the script leaves it out. An account that may create tables gets one as before.
