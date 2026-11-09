using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AppBlocks.Connections;
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
    /// name, or <c>ConnectionStrings:&lt;name&gt;</c> with <c>ConnectionStrings:&lt;name&gt;_ProviderName</c>, which takes every name
    /// <see cref="ConnectionCatalog.Provider"/> does and is SQL Server when a connection string is set without one, as Umbraco reads it,
    /// read from the provider's configuration when a connection is created. Without a connection string it is a SQLite file
    /// <paramref name="defaultFile"/> (default <c>&lt;name&gt;.sqlite.db</c>) in the instance data folder, resolved as
    /// <see cref="ApplicationDatabase.DataFile(IServiceProvider, string)"/> resolves it, with its folder created.
    /// The first time a connection to a given connection string opens in this process, wherever that string came from, the
    /// schema is ensured on a separate connection to the same database before the caller proceeds; a failed ensure fails
    /// that open and the next open tries again. Testing the connection in the backoffice opens it without ensuring anything.
    /// </summary>
    public static AppBlocksBuilder AddApplicationDatabase(this AppBlocksBuilder connections, string name, ApplicationSchema? schema = null,
        string? defaultFile = null)
    {
        connections.AddConnection(name, provider => Create(provider, name, defaultFile, forUse: true));
        connections.LookOnlyConnections[name] = provider => Create(provider, name, defaultFile, forUse: false);
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
    /// version table (<see cref="ApplicationSchema.Table"/>, <c>APP_SCHEMA</c> by default, with <c>SCRIPT</c>, <c>HASH</c> and
    /// <c>APPLIED_AT</c>) if missing, runs every script of the connection's dialect in file-name order, each in its own transaction
    /// with its row, then the run-once scripts of its <c>migrations</c> folder that have no row yet, then the C# steps. Ensure
    /// scripts are idempotent, so every process start runs them all; the table records each one's SHA-256 and when the last start
    /// applied it. A run-once script or step runs once per database. Every row begins with the schema's <see cref="ApplicationSchema.Name"/>:
    /// <c>&lt;name&gt;/&lt;file&gt;</c>, <c>&lt;name&gt;/migrations/&lt;file&gt;</c> or <c>&lt;name&gt;/once/&lt;step&gt;</c>, so schemas
    /// sharing a database keep their own rows. A row written before rows carried the name, such as <c>migrations/&lt;file&gt;</c>, is taken
    /// over by the first schema that expects it, so an existing database runs nothing again. A run-once script whose text no longer
    /// matches its recorded hash stops the ensure with an <see cref="ApplicationSchemaException"/> naming it. Any failure throws
    /// <see cref="ApplicationSchemaException"/> naming the script, and the next call tries again.
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

    /// <summary>The schema folder name for a connection, by its type (<see cref="DatabaseKinds.Of"/> and <see cref="DatabaseKinds.SchemaFolder"/>).</summary>
    public static string Dialect(DbConnection connection) => DatabaseKinds.Of(connection).SchemaFolder();

    private static DbConnection Create(IServiceProvider provider, string name, string? defaultFile, bool forUse)
    {
        // A catalog has already answered for a configured or saved string by the time this runs; a builder with no catalog behind it still has the configuration.
        if (ConnectionCatalog.Configured(provider.GetRequiredService<IConfiguration>(), name) is { } configured) return ConnectionCatalog.Create(configured);
        var file = ApplicationDatabase.DataFile(provider, defaultFile ?? name + ".sqlite.db");
        // SQLite creates the file on open but not its folder. Something only looking, such as the setup screen, leaves the disk as it found it.
        if (forUse) Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file }.ToString());
    }

    /// <summary>A named application's SQLite file beside the CMS SQLite file, or under App_Data without a file-backed SQLite CMS.</summary>
    public static string DataFile(IServiceProvider provider, string fileName)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var environment = provider.GetRequiredService<IWebHostEnvironment>();
        var path = LocalDatabaseFiles.PathFor(configuration, environment, fileName);
        var previous = LocalDatabaseFiles.FallbackPath(configuration, environment, fileName);
        // Changing the default location must not silently replace an existing application's data with an empty file.
        return !File.Exists(path) && File.Exists(previous) ? previous : path;
    }

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
            var migrations = schema.Migrations(dialect);
            var table = schema.Table;
            await ExecuteAsync(connection, null, dialect, VersionTable(dialect, table), sync, ct);
            foreach (var (name, content) in scripts) await RunAsync(connection, dialect, table, schema.Name + "/" + name, name, content, sync, ct);
            var applied = migrations.Count > 0 || schema.Steps.Any(step => step.Once is not null)
                ? await AppliedAsync(connection, table, sync, ct) : new Dictionary<string, string>();
            var ran = scripts.Count;
            foreach (var (name, content) in migrations)
            {
                var legacy = ApplicationSchema.MigrationsFolder + "/" + name;
                var key = schema.Name + "/" + legacy;
                var label = $"run-once script {dialect}/{legacy} of schema {schema.Name}";
                var hash = MigrationHash(content);
                if (await RecordedAsync(connection, dialect, table, applied, key, legacy, hash, sync, ct) is { } recorded) { Unchanged(label, table, recorded, hash); continue; }
                var statements = Statements(dialect, Text(content)).ToArray();
                if (await RunOnceAsync(connection, dialect, table, key, hash, label, async transaction => {
                        foreach (var statement in statements) await ExecuteAsync(connection, transaction, dialect, statement, sync, ct);
                    }, sync, ct)) ran++;
            }
            foreach (var step in schema.Steps)
            {
                if (step.Once is { } once)
                {
                    var legacy = "once/" + step.Name;
                    var hash = MigrationHash(Encoding.UTF8.GetBytes(step.Name!));
                    if (await RecordedAsync(connection, dialect, table, applied, schema.Name + "/" + legacy, legacy, hash, sync, ct) is not null) continue;
                    await RunOnceAsync(connection, dialect, table, schema.Name + "/" + legacy, hash, $"run-once step {step.Name} of schema {schema.Name}",
                        transaction => { once(connection, transaction, dialect); return Task.CompletedTask; }, sync, ct);
                    continue;
                }
                try { step.Always!(connection, dialect); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw new ApplicationSchemaException($"A step of schema {schema.Id} failed: {error.Message}", error);
                }
            }
            memo.Done = true;
            return ran;
        }
        finally { memo.Gate.Release(); }
    }

    /// <summary>
    /// One script with its version row <paramref name="key"/> in one transaction, so a failed script leaves neither. The row the
    /// script had before rows carried the schema's name, its bare file name <paramref name="name"/>, goes with it.
    /// </summary>
    private static async Task RunAsync(DbConnection connection, string dialect, string table, string key, string name, byte[] content, bool sync, CancellationToken ct)
    {
        var transaction = sync ? connection.BeginTransaction() : await connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var statement in Statements(dialect, Text(content)))
            {
                try { await ExecuteAsync(connection, transaction, dialect, statement, sync, ct); }
                catch (SqliteException error) when (error.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase) && AddColumn.IsMatch(statement)) { }
            }
            // Replaced rather than updated, because a row count is not reliable under SQL Server's NOCOUNT.
            await ExecuteAsync(connection, transaction, dialect, $"DELETE FROM {table} WHERE SCRIPT IN ({Marker(dialect, "script")}, {Marker(dialect, "legacy")})",
                sync, ct, ("script", key), ("legacy", name));
            await ExecuteAsync(connection, transaction, dialect,
                $"INSERT INTO {table} (SCRIPT, HASH, APPLIED_AT) VALUES ({Marker(dialect, "script")}, {Marker(dialect, "hash")}, {Marker(dialect, "at")})",
                sync, ct, ("script", key), ("hash", Convert.ToHexString(SHA256.HashData(content))), ("at", DateTime.UtcNow));
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

    private static string Text(byte[] content) => Encoding.UTF8.GetString(content).TrimStart('﻿');

    /// <summary>
    /// A run-once script's hash: SHA-256 of its text with any byte order mark removed and CRLF line endings read as LF, so a checkout
    /// that converts line endings is the same script.
    /// </summary>
    internal static string MigrationHash(byte[] content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text(content).Replace("\r\n", "\n"))));

    /// <summary>A run-once script that ran under one hash and now has another is not run again, and the ensure stops until someone decides.</summary>
    private static void Unchanged(string label, string table, string recorded, string hash)
    {
        if (!recorded.Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new ApplicationSchemaException($"The {label} has changed since it ran (recorded hash {recorded}, now {hash}). A script that ran once is not run again: "
                + $"restore it as it was and put the change in a new script, or, after checking the database by hand, delete its {table} row so it runs again.");
    }

    /// <summary>The hashes of what the version table records, by script.</summary>
    private static async Task<Dictionary<string, string>> AppliedAsync(DbConnection connection, string table, bool sync, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT SCRIPT, HASH FROM {table}";
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = sync ? command.ExecuteReader() : await command.ExecuteReaderAsync(ct);
        while (sync ? reader.Read() : await reader.ReadAsync(ct)) applied[reader.GetString(0)] = reader.GetString(1);
        return applied;
    }

    /// <summary>
    /// The hash recorded for <paramref name="key"/>, or null when nothing has run under it. A row written before rows carried the schema's
    /// name, <paramref name="legacy"/>, with the same hash is renamed to <paramref name="key"/> and counts; one with another hash is left for
    /// the schema it belongs to and is returned, so the caller stops as for a changed script rather than run something that may have run.
    /// </summary>
    private static async Task<string?> RecordedAsync(DbConnection connection, string dialect, string table, Dictionary<string, string> applied,
        string key, string legacy, string hash, bool sync, CancellationToken ct)
    {
        if (applied.TryGetValue(key, out var recorded)) return recorded;
        if (!applied.TryGetValue(legacy, out var earlier)) return null;
        if (!earlier.Equals(hash, StringComparison.OrdinalIgnoreCase)) return earlier;
        // Another process may have renamed it first, which leaves this one renaming nothing.
        await ExecuteAsync(connection, null, dialect, $"UPDATE {table} SET SCRIPT = {Marker(dialect, "key")} WHERE SCRIPT = {Marker(dialect, "legacy")}",
            sync, ct, ("key", key), ("legacy", legacy));
        applied.Remove(legacy);
        applied[key] = earlier;
        return earlier;
    }

    /// <summary>
    /// Runs something once with its version row in one transaction. The row goes in first, so a second process starting at the same
    /// moment waits on it and then finds it, rather than running the work twice. Oracle commits around DDL, so there the row goes in last,
    /// as only a finished script may leave one. Returns false when another process had already recorded it.
    /// </summary>
    private static async Task<bool> RunOnceAsync(DbConnection connection, string dialect, string table, string key, string hash, string label,
        Func<DbTransaction, Task> work, bool sync, CancellationToken ct)
    {
        var rowFirst = dialect != "oracle";
        var transaction = sync ? connection.BeginTransaction() : await connection.BeginTransactionAsync(ct);
        try
        {
            if (rowFirst)
            {
                try { await RecordAsync(connection, transaction, dialect, table, key, hash, sync, ct); }
                catch (DbException) when (!ct.IsCancellationRequested)
                {
                    // Most likely another process recorded it while this one waited; its row decides.
                    if (sync) transaction.Rollback(); else await transaction.RollbackAsync(ct);
                    if ((await AppliedAsync(connection, table, sync, ct)).TryGetValue(key, out var recorded)) { Unchanged(label, table, recorded, hash); return false; }
                    throw;
                }
            }
            await work(transaction);
            if (!rowFirst) await RecordAsync(connection, transaction, dialect, table, key, hash, sync, ct);
            if (sync) transaction.Commit(); else await transaction.CommitAsync(ct);
            return true;
        }
        catch (Exception error) when (error is not (OperationCanceledException or ApplicationSchemaException))
        {
            throw new ApplicationSchemaException($"The {label} failed: {error.Message}", error);
        }
        finally
        {
            if (sync) transaction.Dispose(); else await transaction.DisposeAsync();
        }
    }

    private static Task<int> RecordAsync(DbConnection connection, DbTransaction transaction, string dialect, string table, string key, string hash, bool sync, CancellationToken ct)
        => ExecuteAsync(connection, transaction, dialect,
            $"INSERT INTO {table} (SCRIPT, HASH, APPLIED_AT) VALUES ({Marker(dialect, "script")}, {Marker(dialect, "hash")}, {Marker(dialect, "at")})",
            sync, ct, ("script", key), ("hash", hash), ("at", DateTime.UtcNow));

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

    /// <summary>The version table's creation; <paramref name="table"/> is a checked identifier (<see cref="ApplicationSchema.RecordIn"/>).</summary>
    private static string VersionTable(string dialect, string table) => dialect switch {
        "sqlite" => $"CREATE TABLE IF NOT EXISTS {table} (SCRIPT TEXT NOT NULL PRIMARY KEY, HASH TEXT NOT NULL, APPLIED_AT TEXT NOT NULL)",
        "sqlserver" => $"IF OBJECT_ID(N'{table}', N'U') IS NULL CREATE TABLE {table} (SCRIPT nvarchar(400) NOT NULL PRIMARY KEY, HASH nvarchar(64) NOT NULL, APPLIED_AT datetime2 NOT NULL)",
        "postgresql" => $"CREATE TABLE IF NOT EXISTS {table} (SCRIPT varchar(400) NOT NULL PRIMARY KEY, HASH varchar(64) NOT NULL, APPLIED_AT timestamptz NOT NULL)",
        // ORA-00955: the name is already used, which is the table this creates.
        _ => $"BEGIN EXECUTE IMMEDIATE 'CREATE TABLE {table} (SCRIPT VARCHAR2(400) NOT NULL PRIMARY KEY, HASH VARCHAR2(64) NOT NULL, APPLIED_AT TIMESTAMP NOT NULL)'; "
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
/// A schema script or step that failed, or a run-once script that changed after it ran. It is a <see cref="DbException"/>, so the
/// data source engine answers as it does for a database it cannot open, and the next open tries the schema again.
/// </summary>
public sealed class ApplicationSchemaException(string message, Exception? inner = null) : DbException(message, inner);
