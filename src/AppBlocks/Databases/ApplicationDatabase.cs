using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AppBlocks.Connections;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace AppBlocks.Databases;

/// <summary>
/// An application's own database: a connection registered with AppBlocks by name, whose schema is ensured
/// the first time the process opens it. An application with its own tables uses this rather than its own
/// create-if-missing code; the sample orders database is one.
/// </summary>
public static class ApplicationDatabase
{
    private static readonly ConcurrentDictionary<(string ConnectionString, string Schema), Memo> Ensured = new();

    /// <summary>SQLite has no <c>ADD COLUMN IF NOT EXISTS</c>; such a statement whose column is there counts as applied.</summary>
    private static readonly Regex AddColumn = new(@"^(?:\s|--[^\n]*|/\*.*?\*/)*ALTER\s+TABLE\s.+\sADD\s",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private sealed class Memo
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public volatile bool Done;
    }

    /// <summary>
    /// Registers connection <paramref name="name"/> with AppBlocks. The connection string is a connection saved under that
    /// name, or <c>ConnectionStrings:&lt;name&gt;</c> with <c>ConnectionStrings:&lt;name&gt;_ProviderName</c> (Microsoft.Data.Sqlite, or
    /// Microsoft.Data.SqlClient, the default when a connection string is set without a provider, as Umbraco does), read
    /// from the provider's configuration when a connection is created. Without a connection string it is a SQLite file
    /// <paramref name="defaultFile"/> (default <c>&lt;name&gt;.sqlite.db</c>) in the instance data folder, resolved as
    /// <see cref="ApplicationDatabase.DataFile(IServiceProvider, string)"/> resolves it, with its folder created.
    /// The first time a connection to a given connection string opens in this process, wherever that string came from, the
    /// schema is ensured on a separate connection to the same database before the caller proceeds; a failed ensure fails
    /// that open and the next open tries again. Testing the connection in the backoffice opens it without ensuring anything.
    /// </summary>
    public static AppBlocksBuilder AddApplicationDatabase(this AppBlocksBuilder connections, string name, ApplicationSchema? schema = null,
        string? defaultFile = null)
    {
        connections.AddConnection(name, provider => Create(provider, name, defaultFile));
        if (schema is not null) connections.Schemas[name] = schema;
        return connections;
    }

    /// <summary>Ensures the schema when <paramref name="connection"/> opens, on another connection from <paramref name="create"/>.</summary>
    internal static DbConnection EnsureOnOpen(DbConnection connection, Func<DbConnection> create, string name, ApplicationSchema schema, IServiceProvider provider)
    {
        // Startup validation creates and disposes a connection without opening it, so nothing happens before an open.
        connection.StateChange += (sender, change) => {
            if (change.CurrentState != ConnectionState.Open || sender is not DbConnection opened || IsEnsured(opened, schema)) return;
            using var separate = create();
            separate.Open();
            if (EnsureOnceAsync(separate, schema, sync: true, default).GetAwaiter().GetResult() is { } scripts)
                provider.GetService<ILoggerFactory>()?.CreateLogger(typeof(ApplicationDatabase)).LogInformation(
                    "Ensured the schema of connection {Connection} ({Dialect}): {Scripts} scripts.", name, Dialect(separate), scripts);
        };
        return connection;
    }

    /// <summary>
    /// Ensures a schema on an open connection, once per process per connection string and schema id: creates the
    /// version table <c>APP_SCHEMA</c> (<c>SCRIPT</c>, <c>HASH</c>, <c>APPLIED_AT</c>) if missing, runs every script of
    /// the connection's dialect in file-name order, each in its own transaction with its row, then the C# steps.
    /// Scripts are idempotent "ensure" scripts, so every process start runs them all; the table records each script's
    /// SHA-256 and when the last start applied it. A failure throws <see cref="ApplicationSchemaException"/> naming the
    /// script, and the next call tries again.
    /// </summary>
    public static Task EnsureAsync(DbConnection connection, ApplicationSchema schema, CancellationToken ct = default)
        => EnsureOnceAsync(connection, schema, sync: false, ct);

    /// <summary>The synchronous <see cref="EnsureAsync(DbConnection, ApplicationSchema, CancellationToken)"/>, for code that runs inside an open.</summary>
    public static void Ensure(DbConnection connection, ApplicationSchema schema) => EnsureOnceAsync(connection, schema, sync: true, default).GetAwaiter().GetResult();

    /// <summary>
    /// Ensures a feature's own schema as <see cref="EnsureAsync(DbConnection, ApplicationSchema, CancellationToken)"/> does, except that an account
    /// without the rights to do so still gets on with tables an administrator created by hand: when the ensure fails and every
    /// one of <paramref name="tables"/> can be read, they count as ensured for this process, and the version table, which nothing
    /// reads back, simply stays out of it. A failure that leaves the tables unusable throws as the ensure did, and the next call tries again.
    /// </summary>
    internal static async Task EnsureOrAcceptAsync(DbConnection connection, ApplicationSchema schema, IReadOnlyList<string> tables, CancellationToken ct)
    {
        try { await EnsureOnceAsync(connection, schema, sync: false, ct); }
        catch (DbException) when (!ct.IsCancellationRequested)
        {
            if (!await DatabaseInspector.TablesReadableAsync(connection, tables, ct)) throw;
            Ensured[(connection.ConnectionString, schema.Id)].Done = true;
        }
    }

    /// <summary>A dialect's scripts as the ensure runs them, one after another, for a person to run by hand. The version table is not part of them.</summary>
    internal static string Script(ApplicationSchema schema, string dialect)
        => string.Concat(schema.Scripts(dialect).Select(script => Encoding.UTF8.GetString(script.Content).TrimStart('﻿')));

    /// <summary>The dialect folder name for a connection, by its type.</summary>
    public static string Dialect(DbConnection connection) => connection switch {
        SqliteConnection => "sqlite",
        SqlConnection => "sqlserver",
        // Also recognize host-supplied PostgreSQL and Oracle connections by name.
        _ => connection.GetType().Name switch {
            "NpgsqlConnection" => "postgresql",
            "OracleConnection" => "oracle",
            _ => throw new InvalidOperationException(
                $"An application database is SQLite, SQL Server, PostgreSQL or Oracle; {connection.GetType().FullName} is none of them."),
        },
    };

    private static DbConnection Create(IServiceProvider provider, string name, string? defaultFile)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        if (configuration.GetConnectionString(name) is { Length: > 0 } connectionString)
        {
            var providerName = configuration[$"ConnectionStrings:{name}_ProviderName"];
            if (string.IsNullOrEmpty(providerName) || providerName.Equals("Microsoft.Data.SqlClient", StringComparison.OrdinalIgnoreCase))
                return new SqlConnection(connectionString);
            if (providerName.Equals("Microsoft.Data.Sqlite", StringComparison.OrdinalIgnoreCase)) return new SqliteConnection(connectionString);
            throw new InvalidOperationException($"ConnectionStrings:{name}_ProviderName is {providerName}; an application database uses Microsoft.Data.Sqlite or Microsoft.Data.SqlClient.");
        }
        var file = ApplicationDatabase.DataFile(provider, defaultFile ?? name + ".sqlite.db");
        // SQLite creates the file on open but not its folder.
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file }.ToString());
    }

    /// <summary>Absolute path of a SQLite file under the site's <c>App_Data</c> folder.</summary>
    public static string DataFile(IServiceProvider provider, string fileName)
        => provider.GetRequiredService<IConfiguration>()["AppBlocks:DataRoot"] is { Length: > 0 } root
            ? Path.Combine(root, "App_Data", fileName)
            : DataFile(provider.GetRequiredService<IWebHostEnvironment>(), fileName);

    public static string DataFile(IWebHostEnvironment environment, string fileName)
        => Path.Combine(environment.ContentRootPath, "App_Data", fileName);

    private static bool IsEnsured(DbConnection connection, ApplicationSchema schema)
        => Ensured.TryGetValue((connection.ConnectionString, schema.Id), out var memo) && memo.Done;

    /// <summary>
    /// The one implementation of both forms. With <paramref name="sync"/> every call is synchronous and the task is
    /// complete when it returns, so <see cref="Ensure"/> never blocks on asynchronous work. Returns the number of
    /// scripts run, or null when this process had already ensured the schema.
    /// </summary>
    private static async Task<int?> EnsureOnceAsync(DbConnection connection, ApplicationSchema schema, bool sync, CancellationToken ct)
    {
        if (connection.State != ConnectionState.Open) throw new InvalidOperationException("A schema is ensured on an open connection.");
        // Read after the open, which is when SqlClient drops a password the string does not persist.
        var memo = Ensured.GetOrAdd((connection.ConnectionString, schema.Id), _ => new Memo());
        if (memo.Done) return null;
        if (sync) memo.Gate.Wait(ct); else await memo.Gate.WaitAsync(ct);
        try
        {
            if (memo.Done) return null;
            var dialect = Dialect(connection);
            var scripts = schema.Scripts(dialect);
            await ExecuteAsync(connection, null, dialect, VersionTable(dialect), sync, ct);
            foreach (var (name, content) in scripts) await RunAsync(connection, dialect, name, content, sync, ct);
            foreach (var step in schema.Steps)
            {
                try { step(connection, dialect); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw new ApplicationSchemaException($"A step of schema {schema.Id} failed: {error.Message}", error);
                }
            }
            memo.Done = true;
            return scripts.Count;
        }
        finally { memo.Gate.Release(); }
    }

    /// <summary>One script with its APP_SCHEMA row in one transaction, so a failed script leaves neither.</summary>
    private static async Task RunAsync(DbConnection connection, string dialect, string name, byte[] content, bool sync, CancellationToken ct)
    {
        var transaction = sync ? connection.BeginTransaction() : await connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var statement in Statements(dialect, Encoding.UTF8.GetString(content).TrimStart('﻿')))
            {
                try { await ExecuteAsync(connection, transaction, dialect, statement, sync, ct); }
                catch (SqliteException error) when (error.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase) && AddColumn.IsMatch(statement)) { }
            }
            // Replaced rather than updated, because a row count is not reliable under SQL Server's NOCOUNT.
            await ExecuteAsync(connection, transaction, dialect, $"DELETE FROM APP_SCHEMA WHERE SCRIPT = {Marker(dialect, "script")}", sync, ct, ("script", name));
            await ExecuteAsync(connection, transaction, dialect,
                $"INSERT INTO APP_SCHEMA (SCRIPT, HASH, APPLIED_AT) VALUES ({Marker(dialect, "script")}, {Marker(dialect, "hash")}, {Marker(dialect, "at")})",
                sync, ct, ("script", name), ("hash", Convert.ToHexString(SHA256.HashData(content))), ("at", DateTime.UtcNow));
            if (sync) transaction.Commit(); else await transaction.CommitAsync(ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new ApplicationSchemaException($"The schema script {dialect}/{name} failed: {error.Message}", error);
        }
        finally
        {
            if (sync) transaction.Dispose(); else await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// A script as the provider can run it: SQL Server batches between lines that are exactly <c>GO</c>, SQLite one
    /// statement at a time, Oracle blocks between lines that are exactly <c>/</c> as SQL*Plus reads them, and PostgreSQL
    /// the whole file, which Npgsql sends as one batch.
    /// </summary>
    private static IEnumerable<string> Statements(string dialect, string script) => dialect switch {
        "sqlserver" => Batches(script, "GO"),
        "oracle" => Batches(script, "/"),
        "sqlite" => SqliteStatements(script),
        _ => string.IsNullOrWhiteSpace(script) ? [] : [script],
    };

    private static IEnumerable<string> Batches(string script, string separator)
    {
        var batches = new List<StringBuilder> { new() };
        foreach (var line in script.Split('\n'))
            if (line.Trim().Equals(separator, StringComparison.OrdinalIgnoreCase)) batches.Add(new());
            else batches[^1].Append(line).Append('\n');
        return batches.Select(batch => batch.ToString()).Where(batch => !string.IsNullOrWhiteSpace(batch));
    }

    /// <summary>
    /// SQLite's own test for a complete statement splits the script, so semicolons in strings, comments and trigger
    /// bodies stay where they are; each statement runs alone so a column that is already there skips only its own.
    /// </summary>
    private static IEnumerable<string> SqliteStatements(string script)
    {
        SQLitePCL.Batteries_V2.Init();
        var start = 0;
        for (var end = script.IndexOf(';'); end >= 0; end = script.IndexOf(';', end + 1))
        {
            if (SQLitePCL.raw.sqlite3_complete(script[start..(end + 1)]) == 0) continue;
            yield return script[start..(end + 1)];
            start = end + 1;
        }
        if (!string.IsNullOrWhiteSpace(script[start..])) yield return script[start..];
    }

    private static string VersionTable(string dialect) => dialect switch {
        "sqlite" => "CREATE TABLE IF NOT EXISTS APP_SCHEMA (SCRIPT TEXT NOT NULL PRIMARY KEY, HASH TEXT NOT NULL, APPLIED_AT TEXT NOT NULL)",
        "sqlserver" => "IF OBJECT_ID(N'APP_SCHEMA', N'U') IS NULL CREATE TABLE APP_SCHEMA (SCRIPT nvarchar(400) NOT NULL PRIMARY KEY, HASH nvarchar(64) NOT NULL, APPLIED_AT datetime2 NOT NULL)",
        "postgresql" => "CREATE TABLE IF NOT EXISTS APP_SCHEMA (SCRIPT varchar(400) NOT NULL PRIMARY KEY, HASH varchar(64) NOT NULL, APPLIED_AT timestamptz NOT NULL)",
        // ORA-00955: the name is already used, which is the table this creates.
        _ => "BEGIN EXECUTE IMMEDIATE 'CREATE TABLE APP_SCHEMA (SCRIPT VARCHAR2(400) NOT NULL PRIMARY KEY, HASH VARCHAR2(64) NOT NULL, APPLIED_AT TIMESTAMP NOT NULL)'; "
            + "EXCEPTION WHEN OTHERS THEN IF SQLCODE != -955 THEN RAISE; END IF; END;",
    };

    private static string Marker(string dialect, string name) => (dialect == "oracle" ? ":" : "@") + name;

    /// <summary>Runs one statement; Oracle binds by position, so parameters are given in the order the text uses them.</summary>
    private static async Task<int> ExecuteAsync(DbConnection connection, DbTransaction? transaction, string dialect, string text, bool sync, CancellationToken ct,
        params (string Name, object Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = dialect == "oracle" ? name : "@" + name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return sync ? command.ExecuteNonQuery() : await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// A schema script or step that failed. It is a <see cref="DbException"/>, so the data source engine answers as it
/// does for a database it cannot open, and the next open tries the schema again.
/// </summary>
public sealed class ApplicationSchemaException(string message, Exception inner) : DbException(message, inner);
