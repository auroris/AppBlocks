using AppBlocks.Connections;

namespace AppBlocks.Databases;

/// <summary>Where a feature's tables stand, as the setup screen shows it.</summary>
public static class DatabaseFeatureState
{
    /// <summary>The tables are in place and this account can use them.</summary>
    public const string Ready = "ready";
    /// <summary>The tables are not there yet, and nothing says the feature cannot create them on first use.</summary>
    public const string Automatic = "automatic";
    /// <summary>An administrator has to grant a permission or run the script.</summary>
    public const string Action = "action";
    /// <summary>This database type cannot hold the feature's tables.</summary>
    public const string Unsupported = "unsupported";
    /// <summary>The connection does not open, or is not registered.</summary>
    public const string Unreachable = "unreachable";
    /// <summary>The site replaced the feature's store with its own, so its tables are the site's business.</summary>
    public const string SiteManaged = "siteManaged";
}

/// <param name="State">Ready, Missing or NoAccess, in lower case.</param>
/// <param name="Required">Whether the feature stops without it, rather than one corner of it.</param>
public sealed record DatabaseFeatureTable(string Name, string State, bool Required);

/// <summary>One feature's database standing for an administrator. It carries names and SQL only, never a connection string.</summary>
/// <param name="Connection">The connection's name as the Connections tab shows it.</param>
/// <param name="Provider">The database type, as <see cref="ConnectionInfo.Provider"/> gives it.</param>
/// <param name="Lost">What stops working without the tables, and what keeps working.</param>
/// <param name="GrantText">How to let AppBlocks create the tables itself.</param>
/// <param name="GrantSql">The statements to run for that, when the database type has them.</param>
/// <param name="HasScript">Whether <see cref="IDatabaseSetupFeature.ScriptAsync"/> has a script for this database type.</param>
public sealed record DatabaseFeatureStatus(string Alias, string Title, string Connection, string Provider, string State, string Summary, string Lost,
    IReadOnlyList<DatabaseFeatureTable> Tables, string? GrantText, string? GrantSql, bool HasScript);

/// <summary>A feature with tables of its own, for the setup screen. Companions add theirs through dependency injection.</summary>
public interface IDatabaseSetupFeature
{
    string Alias { get; }

    /// <summary>Where the feature stands. It changes nothing: no table, no file and no rows.</summary>
    Task<DatabaseFeatureStatus> StatusAsync(CancellationToken ct);

    /// <summary>The script that creates the feature's tables by hand, from the same source the feature installs from; null when there is none.</summary>
    Task<string?> ScriptAsync(CancellationToken ct);
}

/// <summary>The wording and SQL the features' setup guidance shares, by database type.</summary>
public static class DatabaseSetupGuide
{
    /// <summary>The placeholder in SQL when the database has not said who the account is.</summary>
    public const string UnknownAccount = "<application account>";

    /// <summary>The placeholder in SQL when the database has not said which schema the account creates tables in.</summary>
    public const string UnknownSchema = "<default schema>";

    /// <summary>Where the setup screen lives, under the site's path base. Its tab opens from the query string.</summary>
    public const string ScreenPath = "/umbraco/section/settings/workspace/appblocks/view/setup";

    /// <summary>The feature's state and summary from what the inspection found.</summary>
    public static (string State, string Summary) Standing(DatabaseInspection inspection, IEnumerable<string> required, bool autoCreate = true)
    {
        if (!inspection.Reachable) return (DatabaseFeatureState.Unreachable, "AppBlocks could not connect to this database. Check the connection on the Connections tab, then refresh.");
        var states = required.Select(inspection.StateOf).ToArray();
        if (states.All(state => state == InspectedTableState.Ready)) return (DatabaseFeatureState.Ready, "The tables are in place and this account can use them.");
        if (states.Contains(InspectedTableState.NoAccess)) return (DatabaseFeatureState.Action, "The tables exist, but this account is not allowed to read and write them.");
        if (!autoCreate) return (DatabaseFeatureState.Action, "The tables are missing, and this site has turned off creating them automatically.");
        return inspection.CanCreateTables == false
            ? (DatabaseFeatureState.Action, "The tables are missing and this account is not allowed to create them.")
            : (DatabaseFeatureState.Automatic, "The tables are not there yet. They will be created the first time someone uses this feature.");
    }

    public static IReadOnlyList<DatabaseFeatureTable> Tables(DatabaseInspection inspection, IEnumerable<string> required, IEnumerable<string>? optional = null)
    {
        DatabaseFeatureTable Row(string name, bool isRequired) => new(name, inspection.StateOf(name) switch {
            InspectedTableState.Ready => "ready", InspectedTableState.NoAccess => "noAccess", _ => "missing" }, isRequired);
        return [.. required.Select(name => Row(name, true)), .. (optional ?? []).Select(name => Row(name, false))];
    }

    /// <summary>How to let AppBlocks create the tables itself on this database type.</summary>
    /// <param name="schema">Where the account's tables would land, as <see cref="DatabaseInspection.Schema"/> gives it. The rights have to be there, because that is what the inspection asks about.</param>
    public static (string Text, string? Sql) Grant(string provider, string? account, string? schema = null) => provider switch {
        "sqlite" => ("SQLite has no accounts. Give the account the site runs under read and write access to the database file and the folder it sits in.", null),
        "sqlServer" => ((account is not null && schema is null
                ? "This account's default schema does not exist, so it cannot create tables. Create that schema, or give the account a default schema that exists, and then run this in the database as its owner with the schema's name filled in. The account can then create tables there and read and write every table there."
                : $"Run this in the database as its owner. It lets the account create tables in {(schema is null ? "its default schema" : $"{schema}, its default schema,")} and read and write every table there.") + " To keep it to these tables alone, use the script instead.",
            $"GRANT CREATE TABLE TO {SqlServerName(account)};\nGRANT ALTER ON SCHEMA::{SqlServerName(schema ?? UnknownSchema)} TO {SqlServerName(account)};\nGRANT SELECT, INSERT, UPDATE, DELETE, REFERENCES ON SCHEMA::{SqlServerName(schema ?? UnknownSchema)} TO {SqlServerName(account)};\n"),
        "postgres" => (account is not null && schema is null
                ? "This account has no schema it can use on its search path. Fill in the one it should use, and make sure that schema is on the path. Run this in the database as its owner. The account then owns the tables it creates."
                : $"Run this in the database as its owner. The account then owns the tables it creates in {schema ?? "its current schema"}.",
            $"GRANT USAGE, CREATE ON SCHEMA {PostgresName(schema ?? UnknownSchema)} TO {PostgresName(account ?? UnknownAccount)};\n"),
        "oracle" or "oracle11g" => ("Run this as a DBA. The account then owns the tables it creates, and needs room for them in its tablespace.",
            (provider == "oracle11g" ? $"GRANT CREATE TABLE, CREATE SEQUENCE, CREATE TRIGGER TO {account ?? UnknownAccount};\n" : $"GRANT CREATE TABLE, CREATE SEQUENCE TO {account ?? UnknownAccount};\n")
                + $"ALTER USER {account ?? UnknownAccount} QUOTA UNLIMITED ON <tablespace>;\n"),
        _ => ("Grant the account the right to create tables.", null),
    };

    /// <summary>A script's comment header: what it is for and how to run it.</summary>
    /// <param name="schema">Where the account's own tables land, for what running this as someone else does differently.</param>
    public static string Header(string title, string connection, string provider, string? account, string? schema = null)
    {
        var how = provider switch {
            "sqlite" => "-- Run it with any SQLite tool against the database file.\n",
            "sqlServer" => "-- Run it in that database as its owner, with sqlcmd or Management Studio: the GO lines separate its batches.\n"
                + (schema is null or "dbo" ? "" : $"-- The account's default schema is {schema}. Run as the owner, this creates the tables in dbo, which SQL Server looks in next, so the account finds them there.\n"),
            "postgres" => "-- Run it in that database as the account the site signs in as, or as an owner who then grants the tables below.\n"
                + (schema is null ? "" : $"-- The account works in schema {schema}. An owner should run this first, so the tables land there: SET search_path TO {PostgresName(schema)};\n"),
            _ => $"-- Run it in SQL*Plus or SQLcl: the / lines run its blocks. Connect as the site's account, or as a DBA after:\n--     ALTER SESSION SET CURRENT_SCHEMA = {account ?? UnknownAccount};\n",
        };
        return $"-- AppBlocks: {title}\n-- Creates this feature's tables in the database behind the connection {connection}.\n{how}-- It is safe to run more than once, and AppBlocks uses the tables without a restart.\n\n";
    }

    /// <summary>Statements as one script, each followed by what ends a statement on this database type's own tool.</summary>
    public static string Join(string provider, IEnumerable<string> statements) => string.Concat(statements.Select(statement => statement.TrimEnd() + (provider switch {
        "sqlServer" => "\nGO\n",
        "oracle" or "oracle11g" => "\n/\n",
        _ => ";\n",
    })));

    /// <summary>The grants that let the account read and write tables someone else created, for a script's end; empty where the account owns them.</summary>
    public static string TableGrants(string provider, string? account, IEnumerable<string> tables)
    {
        // An account that is the database's owner already has every right, and SQL Server refuses to grant to itself.
        if (provider == "sqlServer" && account is "dbo") return "";
        var rights = tables.Select(table => provider switch {
            "sqlServer" => $"GRANT SELECT, INSERT, UPDATE, DELETE ON {table} TO {SqlServerName(account)};\n",
            "postgres" => $"GRANT SELECT, INSERT, UPDATE, DELETE ON \"{table}\" TO {PostgresName(account ?? UnknownAccount)};\n",
            _ => "",
        });
        var text = string.Concat(rights);
        return text.Length == 0 ? "" : $"\n-- Lets the account the site signs in as use the tables.\n{text}" + (provider == "sqlServer" ? "GO\n" : "");
    }

    private static string SqlServerName(string? name) => "[" + (name ?? UnknownAccount).Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string PostgresName(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
