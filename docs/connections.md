# Connections

**Settings → AppBlocks → Connections** supplies **AppBlocks database** (`AppBlocksDb`) as the default for forms and reports. On SQLite installations, its `AppBlocks.sqlite.db` file sits beside Umbraco's SQLite database. Application data, saved report views and uploaded file contents share this file by default. It is created on first use with foreign keys and write-ahead logging enabled. The site supplies its business tables; saved-report and upload tables are created by their stores, or by hand where the database account may not (see [Database setup](database-setup.md)).

Use this separate database for application work. SQLite permits only one writer per database, so keeping form submissions and uploads out of Umbraco's file avoids competing with CMS saves. `UmbracoDb` remains available for sources that specifically read CMS data. AppBlocks opens its own connections; it does not share Umbraco's scope or transaction.

If Umbraco has no file-backed SQLite database, the default file is under `App_Data` in `AppBlocks:DataRoot`, or the site's content root when that is not set. A configured `ConnectionStrings:AppBlocksDb` and `ConnectionStrings:AppBlocksDb_ProviderName` override the default file and provider. Back up the application database as well as Umbraco's database, using a SQLite-aware backup when the application is running.

Uploads can use their own store by setting:

```json
{
  "Attachments": {
    "Connection": "AttachmentsDb"
  }
}
```

This selects the optional `Attachments.sqlite.db` beside Umbraco's SQLite file. Set `Attachments:Connection` to another registered SQLite or SQL Server connection to use another database, or configure `ConnectionStrings:AttachmentsDb` and its `_ProviderName` to override that optional store. Leave `Attachments:Connection` empty or use `AppBlocksDb` to share the default application database. Uploaded bytes are stored in database tables, not loose files. If the application database uses PostgreSQL or Oracle, select a separate supported upload store.

The reports companion defaults `InteractiveReport:SavedReports:Connection` to `AppBlocksDb`. An explicit `SavedReports:Connection` or `SavedReports:DataSource` takes precedence. See the [companion guide](../src/AppBlocks.InteractiveReports/README.md).

## Database drivers

AppBlocks ships no database driver. SQLite and SQL Server need nothing more, because Umbraco itself brings `Microsoft.Data.Sqlite` and `Microsoft.Data.SqlClient` into every site. For the other databases, the site installs the driver package and AppBlocks finds it by name at run time, as Interactive Reports does:

| Database | Package the site installs |
| --- | --- |
| SQLite | none; comes with Umbraco |
| SQL Server | none; comes with Umbraco |
| PostgreSQL | `Npgsql` |
| Oracle 11.2 and Oracle 12+ | `Oracle.ManagedDataAccess.Core` |

Without the package, saving or testing a PostgreSQL or Oracle connection fails with a message naming it, and a configured or code-registered connection of that type fails the same way when first opened. Install the package and restart the site.

The Oracle driver needs one more setting. Umbraco's startup scan loads every assembly beside the site and whatever those reference, and `Oracle.ManagedDataAccess` references optional cloud assemblies its package does not ship, so the site cannot start until the scan leaves the driver alone:

```json
{
  "Umbraco": {
    "CMS": {
      "TypeFinder": {
        "AdditionalAssemblyExclusionEntries": ["Oracle.ManagedDataAccess"]
      }
    }
  }
}
```

AppBlocks still finds the driver by name when a connection opens. Npgsql needs no such setting.

Administrators can add, edit and test extra SQL Server, SQLite, PostgreSQL, **Oracle 11.2** or **Oracle 12+** connections. The screen links to [connection string examples](https://www.connectionstrings.com/). Report table choices come from database metadata; SQL queries are authored on the report block. Changing a connection selects a database; it does not copy existing tables or files.

Connection strings entered here are protected with ASP.NET Core Data Protection and stored in `App_Data/AppBlocks/connections.json`; they are never stored in blocks, returned by the listing API or included in content exports. Keep that file and the site's Data Protection keys together when backing up or moving an installation. Multiple application instances need a shared, durable file/key store; concurrent administration across multiple instances is not yet coordinated.

Register a site's business connections with `AddAppBlocks(connections => connections.AddConnection(...))`, or `AddApplicationDatabase` for a database with its own schema. Connections registered this way and those supplied by the site configuration are read-only in this screen, and forms, data sources and the report source picker offer them like any other. A registered connection has no connection string to show: its database type is the dialect passed to `AddConnection`, or else what its unopened connection's type says, and `unknown` for a custom connection type that declares no dialect. A saved connection of the same name keeps precedence and stays editable.

Connections added directly to Interactive Reports' builder through the companion's `AddAppBlocksInteractiveReports` are outside this list. Configured reports, report definition nodes and forms can still open one by name, but this screen does not show it and a report block refuses it.

**Oracle 11.2** and **Oracle 12+** are Oracle SQL compatibility modes shared with Interactive Reports. The choice controls the SQL generated for the connection. Both modes open the site's `Oracle.ManagedDataAccess.Core` driver (see [Database drivers](#database-drivers)). For configured connections, set `ConnectionStrings:<name>_ProviderName` to `oracle11g` for Oracle 11.2 mode or `oracle` for Oracle 12+ mode; existing `Oracle.ManagedDataAccess.Client` and `Oracle.ManagedDataAccess.Core` provider names still work. Code registrations can pass `DatabaseDialect.Oracle11g` or `DatabaseDialect.Oracle` to `AddConnection`. Oracle 12+ mode also detects pre-12 server versions on first use and switches to Oracle 11.2 mode, as Interactive Reports does.

Oracle 11.2 mode uses 11.2-compatible metadata and row limits, with explicit key sequences for generated keys (`keySequences`, for example `{"ID":"ORDERS_SEQ"}`). Oracle 12+ mode additionally discovers identity columns. The reports companion preserves the selected compatibility mode. Upload storage still requires a separate SQLite or SQL Server connection.

AppBlocks is tested with the `Oracle.ManagedDataAccess.Core` version Interactive Reports' live tests use. Oracle's [current driver certification](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/InstallSystemRequirements.html) lists Database 19c and later; connectivity to older servers requires verification independently of the selected SQL compatibility mode.
