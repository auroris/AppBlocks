using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AppBlocks.Authentication;
using AppBlocks.Connections;
using Microsoft.Extensions.Options;
using AppBlocks.Databases;

namespace AppBlocks.Attachments;

/// <summary>The record a file belongs to: a form resource's alias and the record's key, as the form endpoints know them.</summary>
public sealed record AttachmentOwner(string Alias, string Key)
{
    /// <summary>What the alias of a <see cref="Pending"/> owner starts with. No resource alias does, so no record key can name pending files.</summary>
    public const string PendingPrefix = "pending:";

    /// <summary>
    /// The owner of files uploaded for one submission of a form whose record is not saved yet: the resource's alias after
    /// <see cref="PendingPrefix"/>, and the submission key followed by the uploader's account key, so only the uploader
    /// reaches the files. An anonymous visitor has no account and is identified by the submission key alone.
    /// </summary>
    public static AttachmentOwner Pending(string resourceAlias, Guid submissionKey, ClaimsPrincipal uploader)
        => new(PendingPrefix + resourceAlias, PendingKey(submissionKey) + (uploader.Identities.Any(identity => identity.IsAuthenticated) ? ApplicationAccessPolicy.UserKey(uploader) : ""));

    /// <summary>What the key of every pending owner of one submission starts with, whoever uploaded.</summary>
    internal static string PendingKey(Guid submissionKey) => submissionKey.ToString("D") + ":";

    public bool IsPending => Alias.StartsWith(PendingPrefix, StringComparison.Ordinal);

    /// <summary>The form resource's alias, also for a pending owner.</summary>
    public string ResourceAlias => IsPending ? Alias[PendingPrefix.Length..] : Alias;
}

/// <summary>One of an owner's files: its current revision, with the earlier ones newest first.</summary>
public sealed record AttachmentItem(long Id, string FileName, string ContentType, long Size, string Sha256, string? Purpose, string? Label, string? Notes,
    string UploadedBy, DateTime UploadedAt, string UpdatedBy, DateTime UpdatedAt, IReadOnlyList<AttachmentRevision> Revisions);

public sealed record AttachmentRevision(long Id, string FileName, string ContentType, long Size, string Sha256, string UploadedBy, DateTime UploadedAt);

public sealed record AttachmentContent(long Id, string FileName, string ContentType, long Size, string Sha256, byte[] Bytes);

public sealed record AttachmentUpload(string FileName, string ContentType, byte[] Bytes);

/// <summary>What a purge deleted: removed links, the files of every revision behind them, and their bytes.</summary>
public sealed record AttachmentPurge(int Links, int Files, long Bytes);

/// <summary>
/// The two attachment tables, in whichever registered connection <see cref="AttachmentSettings.Connection"/> names.
/// Every method takes the owner and checks it in SQL: a file id that is not the current revision, or an earlier
/// revision, of one of that owner's live links is simply not found, so a readable record can never be paired with
/// another record's file. The store runs on SQLite, SQL Server, PostgreSQL and Oracle 12c or later, so the files can sit beside
/// the application's data; the PostgreSQL and Oracle scripts and SQL are written from each database's documentation and have not
/// been run against a live server (see <see cref="Sql"/>). Every way the database can refuse the store surfaces as an
/// <see cref="AttachmentStoreException"/>, and a problem that needs an administrator is logged once, with what to do, until it goes away.
/// </summary>
public sealed partial class AttachmentStore(IApplicationConnectionFactory connections, IOptions<AttachmentSettings> settings, ILogger<AttachmentStore> logger)
{
    public static readonly ApplicationSchema Schema = ApplicationSchema.FromEmbedded(typeof(AttachmentStore).Assembly, "Attachments/Schema");

    /// <summary>The tables the store reads and writes, which an administrator can also create from the schema's script.</summary>
    public static readonly IReadOnlyList<string> Tables = ["APP_ATTACHMENT", "APP_ATTACHMENT_LINK"];

    /// <summary>Which problem has been logged per connection name, because a request's store does not outlive the request.</summary>
    private static readonly ConcurrentDictionary<string, AttachmentStoreProblem> Reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The chain of revisions behind every live link of an owner: depth 0 is the current file.</summary>
    private const string Chain = """
        WITH chain (ROOT_ID, ATTACHMENT_ID, DEPTH) AS (
            SELECT l.ATTACHMENT_ID, l.ATTACHMENT_ID, 0 FROM APP_ATTACHMENT_LINK l
            WHERE l.OWNER_ALIAS = @alias AND l.OWNER_KEY = @key AND l.REMOVED_AT IS NULL
            UNION ALL
            SELECT c.ROOT_ID, a.REPLACES, c.DEPTH + 1 FROM chain c JOIN APP_ATTACHMENT a ON a.ATTACHMENT_ID = c.ATTACHMENT_ID
            WHERE a.REPLACES IS NOT NULL)
        """;

    public Task<IReadOnlyList<AttachmentItem>> ListAsync(AttachmentOwner owner, CancellationToken ct) => GuardAsync(write: false, async () =>
    {
        await using var connection = await OpenAsync(ct);
        return await ListAsync(connection, null, owner, null, ct);
    });

    /// <summary>The current file or an earlier revision of one of the owner's live links, with its bytes; null when it is not one.</summary>
    public Task<AttachmentContent?> ReadAsync(AttachmentOwner owner, long id, CancellationToken ct) => GuardAsync<AttachmentContent?>(write: false, async () =>
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, null, Chain + """

            SELECT a.ATTACHMENT_ID, a.FILE_NAME, a.CONTENT_TYPE, a.SIZE, a.SHA256, a.CONTENT
            FROM chain c JOIN APP_ATTACHMENT a ON a.ATTACHMENT_ID = c.ATTACHMENT_ID WHERE a.ATTACHMENT_ID = @id
            """ + Recursion(connection), ("@alias", owner.Alias), ("@key", owner.Key), ("@id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new AttachmentContent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), (byte[])reader.GetValue(5));
    });

    public Task<AttachmentItem> AddAsync(AttachmentOwner owner, AttachmentUpload file, string? purpose, string? label, string? notes, string by, CancellationToken ct) => GuardAsync(write: true, async () =>
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var id = await InsertFileAsync(connection, transaction, file, by, now, replaces: null, ct);
        await using var link = Command(connection, transaction, """
            INSERT INTO APP_ATTACHMENT_LINK (OWNER_ALIAS, OWNER_KEY, ATTACHMENT_ID, PURPOSE, LABEL, NOTES, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@alias, @key, @id, @purpose, @label, @notes, @by, @now, @by, @now)
            """, ("@alias", owner.Alias), ("@key", owner.Key), ("@id", id), ("@purpose", Trim(purpose)), ("@label", Trim(label)), ("@notes", Trim(notes)), ("@by", by), ("@now", now));
        await link.ExecuteNonQueryAsync(ct);
        var item = (await ListAsync(connection, transaction, owner, id, ct)).Single();
        await transaction.CommitAsync(ct);
        return item;
    });

    /// <summary>
    /// A new revision of the file whose current id is <paramref name="id"/>, or new metadata for it, or both. A metadata
    /// value that is null is kept; an empty string clears it. Null when the id is not an owner's current file.
    /// </summary>
    public Task<AttachmentItem?> ReplaceAsync(AttachmentOwner owner, long id, AttachmentUpload? file, string? purpose, string? label, string? notes, string by, CancellationToken ct) => GuardAsync<AttachmentItem?>(write: true, async () =>
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await LinkAsync(connection, transaction, owner, id, ct) is not { } linkId) return null;
        var now = DateTime.UtcNow;
        var current = id;
        if (file is not null) current = await InsertFileAsync(connection, transaction, file, by, now, replaces: id, ct);
        var set = new List<string> { "ATTACHMENT_ID = @current", "UPDATED_BY = @by", "UPDATED_AT = @now" };
        var values = new List<(string, object?)> { ("@current", current), ("@by", by), ("@now", now), ("@link", linkId) };
        foreach (var (column, value, name) in new[] { ("PURPOSE", purpose, "@purpose"), ("LABEL", label, "@label"), ("NOTES", notes, "@notes") })
        {
            if (value is null) continue;
            set.Add($"{column} = {name}");
            values.Add((name, Trim(value)));
        }
        await using var update = Command(connection, transaction, $"UPDATE APP_ATTACHMENT_LINK SET {string.Join(", ", set)} WHERE LINK_ID = @link", values.ToArray());
        await update.ExecuteNonQueryAsync(ct);
        var item = (await ListAsync(connection, transaction, owner, current, ct)).Single();
        await transaction.CommitAsync(ct);
        return item;
    });

    /// <summary>Removes the link whose current file is <paramref name="id"/>: it leaves the lists, and its rows stay for the purge. False when it is not an owner's current file.</summary>
    public Task<bool> RemoveAsync(AttachmentOwner owner, long id, string by, CancellationToken ct) => GuardAsync(write: true, async () =>
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, null, """
            UPDATE APP_ATTACHMENT_LINK SET REMOVED_BY = @by, REMOVED_AT = @now
            WHERE OWNER_ALIAS = @alias AND OWNER_KEY = @key AND ATTACHMENT_ID = @id AND REMOVED_AT IS NULL
            """, ("@by", by), ("@now", DateTime.UtcNow), ("@alias", owner.Alias), ("@key", owner.Key), ("@id", id));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    });

    /// <summary>Deletes the links removed before <paramref name="removedBefore"/> with every revision behind them. Earlier revisions of live links stay.</summary>
    public Task<AttachmentPurge> PurgeAsync(DateTime removedBefore, CancellationToken ct) => GuardAsync(write: true, async () =>
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var purge = await DeleteLinksAsync(connection, transaction,
            "SELECT LINK_ID, ATTACHMENT_ID FROM APP_ATTACHMENT_LINK WHERE REMOVED_AT IS NOT NULL AND REMOVED_AT < @before", ct, ("@before", removedBefore));
        await transaction.CommitAsync(ct);
        if (purge.Links > 0) logger.LogInformation("Purged {Links} removed attachment link(s) and {Files} file(s), {Bytes} bytes.", purge.Links, purge.Files, purge.Bytes);
        return purge;
    });

    /// <summary>
    /// Gives the pending owner's live files to <paramref name="owner"/>, the record its form has saved, and returns how many moved.
    /// The files keep their ids, so a list the runtime holds stays valid.
    /// </summary>
    public Task<int> AdoptAsync(AttachmentOwner pending, AttachmentOwner owner, string by, CancellationToken ct) => GuardAsync(write: true, async () =>
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, null, """
            UPDATE APP_ATTACHMENT_LINK SET OWNER_ALIAS = @alias, OWNER_KEY = @key, UPDATED_BY = @by, UPDATED_AT = @now
            WHERE OWNER_ALIAS = @pendingAlias AND OWNER_KEY = @pendingKey AND REMOVED_AT IS NULL
            """, ("@alias", owner.Alias), ("@key", owner.Key), ("@by", by), ("@now", DateTime.UtcNow), ("@pendingAlias", pending.Alias), ("@pendingKey", pending.Key));
        return await command.ExecuteNonQueryAsync(ct);
    });

    /// <summary>Whether another uploader than <paramref name="pending"/>'s has live files pending under the same submission key.</summary>
    public Task<bool> PendingForOthersAsync(AttachmentOwner pending, Guid submissionKey, CancellationToken ct) => GuardAsync(write: false, async () =>
    {
        await using var connection = await OpenAsync(ct);
        // The submission key is a GUID and the colon after it is no LIKE wildcard, so the prefix matches literally in every database.
        await using var command = Command(connection, null, """
            SELECT COUNT(*) FROM APP_ATTACHMENT_LINK
            WHERE OWNER_ALIAS = @alias AND OWNER_KEY LIKE @submission AND OWNER_KEY <> @key AND REMOVED_AT IS NULL
            """, ("@alias", pending.Alias), ("@submission", AttachmentOwner.PendingKey(submissionKey) + "%"), ("@key", pending.Key));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    });

    /// <summary>
    /// Deletes the pending files uploaded more than <see cref="AttachmentSettings.PendingLifetimeHours"/> ago, whose form was never
    /// saved, with their rows. The pending endpoints call it before each operation, so no background service is needed.
    /// </summary>
    public Task<int> ExpirePendingAsync(CancellationToken ct) => GuardAsync(write: true, async () =>
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var expired = await DeleteLinksAsync(connection, transaction, "SELECT LINK_ID, ATTACHMENT_ID FROM APP_ATTACHMENT_LINK WHERE OWNER_ALIAS LIKE @pending AND CREATED_AT < @before", ct,
            ("@pending", AttachmentOwner.PendingPrefix + "%"),
            ("@before", DateTime.UtcNow.AddHours(-Math.Max(0, settings.Value.PendingLifetimeHours))));
        await transaction.CommitAsync(ct);
        return expired.Links;
    });

    /// <summary>Deletes the links a query selects, each with every revision behind it.</summary>
    private static async Task<AttachmentPurge> DeleteLinksAsync(DbConnection connection, DbTransaction transaction, string select, CancellationToken ct, params (string Name, object? Value)[] values)
    {
        var links = new List<(long Link, long Attachment)>();
        await using (var selected = Command(connection, transaction, select, values))
        await using (var reader = await selected.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) links.Add((reader.GetInt64(0), reader.GetInt64(1)));
        var files = 0; var bytes = 0L;
        foreach (var (link, attachment) in links)
        {
            // The link references the current file and each file the one it replaced, so delete the link, then the files newest first.
            var chain = new List<(long Id, long Size)>();
            await using (var revisions = Command(connection, transaction, """
                WITH chain (ATTACHMENT_ID, DEPTH) AS (
                    SELECT ATTACHMENT_ID, 0 FROM APP_ATTACHMENT WHERE ATTACHMENT_ID = @id
                    UNION ALL
                    SELECT a.REPLACES, c.DEPTH + 1 FROM chain c JOIN APP_ATTACHMENT a ON a.ATTACHMENT_ID = c.ATTACHMENT_ID WHERE a.REPLACES IS NOT NULL)
                SELECT a.ATTACHMENT_ID, a.SIZE FROM chain c JOIN APP_ATTACHMENT a ON a.ATTACHMENT_ID = c.ATTACHMENT_ID ORDER BY c.DEPTH
                """ + Recursion(connection), ("@id", attachment)))
            await using (var reader = await revisions.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) chain.Add((reader.GetInt64(0), reader.GetInt64(1)));
            await using (var delete = Command(connection, transaction, "DELETE FROM APP_ATTACHMENT_LINK WHERE LINK_ID = @link", ("@link", link)))
                await delete.ExecuteNonQueryAsync(ct);
            foreach (var (id, size) in chain)
            {
                await using var delete = Command(connection, transaction, "DELETE FROM APP_ATTACHMENT WHERE ATTACHMENT_ID = @id", ("@id", id));
                files += await delete.ExecuteNonQueryAsync(ct);
                bytes += size;
            }
        }
        return new AttachmentPurge(links.Count, files, bytes);
    }

    /// <summary>The owner's items (one when <paramref name="current"/> names a current file), newest first, each with its revisions.</summary>
    private async Task<IReadOnlyList<AttachmentItem>> ListAsync(DbConnection connection, DbTransaction? transaction, AttachmentOwner owner, long? current, CancellationToken ct)
    {
        var links = new Dictionary<long, (string? Purpose, string? Label, string? Notes, string UpdatedBy, DateTime UpdatedAt, long Order)>();
        (string, object?)[] selection = current is null ? [("@alias", owner.Alias), ("@key", owner.Key)] : [("@alias", owner.Alias), ("@key", owner.Key), ("@id", current)];
        await using (var command = Command(connection, transaction, """
            SELECT ATTACHMENT_ID, PURPOSE, LABEL, NOTES, UPDATED_BY, UPDATED_AT, LINK_ID FROM APP_ATTACHMENT_LINK
            WHERE OWNER_ALIAS = @alias AND OWNER_KEY = @key AND REMOVED_AT IS NULL
            """ + (current is null ? "" : " AND ATTACHMENT_ID = @id"), selection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                links[reader.GetInt64(0)] = (Null(reader, 1), Null(reader, 2), Null(reader, 3), reader.GetString(4), reader.GetDateTime(5), reader.GetInt64(6));
        if (links.Count == 0) return [];
        var revisions = new Dictionary<long, List<AttachmentRevision>>();
        await using (var command = Command(connection, transaction, Chain + """

            SELECT c.ROOT_ID, a.ATTACHMENT_ID, a.FILE_NAME, a.CONTENT_TYPE, a.SIZE, a.SHA256, a.UPLOADED_BY, a.UPLOADED_AT
            FROM chain c JOIN APP_ATTACHMENT a ON a.ATTACHMENT_ID = c.ATTACHMENT_ID ORDER BY c.ROOT_ID, c.DEPTH
            """ + Recursion(connection), ("@alias", owner.Alias), ("@key", owner.Key)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var root = reader.GetInt64(0);
                if (!links.ContainsKey(root)) continue;
                if (!revisions.TryGetValue(root, out var list)) revisions[root] = list = [];
                list.Add(new(reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetDateTime(7)));
            }
        return links.OrderByDescending(l => l.Value.Order).Select(l => {
            var chain = revisions[l.Key];
            var head = chain[0];
            return new AttachmentItem(head.Id, head.FileName, head.ContentType, head.Size, head.Sha256, l.Value.Purpose, l.Value.Label, l.Value.Notes,
                head.UploadedBy, head.UploadedAt, l.Value.UpdatedBy, l.Value.UpdatedAt, chain.Skip(1).ToArray());
        }).ToArray();
    }

    private static async Task<long?> LinkAsync(DbConnection connection, DbTransaction transaction, AttachmentOwner owner, long id, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT LINK_ID FROM APP_ATTACHMENT_LINK WHERE OWNER_ALIAS = @alias AND OWNER_KEY = @key AND ATTACHMENT_ID = @id AND REMOVED_AT IS NULL",
            ("@alias", owner.Alias), ("@key", owner.Key), ("@id", id));
        return await command.ExecuteScalarAsync(ct) is { } value and not DBNull ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : null;
    }

    private static async Task<long> InsertFileAsync(DbConnection connection, DbTransaction transaction, AttachmentUpload file, string by, DateTime now, long? replaces, CancellationToken ct)
    {
        var kind = DatabaseKinds.Of(connection);
        var sql = "INSERT INTO APP_ATTACHMENT (FILE_NAME, CONTENT_TYPE, SIZE, SHA256, CONTENT, UPLOADED_BY, UPLOADED_AT, REPLACES)"
            + (kind == DatabaseDialect.SqlServer ? " OUTPUT INSERTED.ATTACHMENT_ID" : "")
            + " VALUES (@name, @type, @size, @hash, @content, @by, @now, @replaces)"
            + kind switch { DatabaseDialect.SqlServer => "", DatabaseDialect.Oracle => " RETURNING ATTACHMENT_ID INTO @id", _ => " RETURNING ATTACHMENT_ID" };
        await using var command = Command(connection, transaction, sql, ("@name", file.FileName), ("@type", file.ContentType), ("@size", (long)file.Bytes.Length),
            ("@hash", Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant()), ("@content", file.Bytes), ("@by", by), ("@now", now), ("@replaces", replaces));
        if (kind != DatabaseDialect.Oracle) return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        // Oracle hands a RETURNING value back through an output parameter, whose value may be the driver's own number type, so it is read as text.
        var id = command.CreateParameter();
        id.ParameterName = "id";
        id.DbType = DbType.Int64;
        id.Direction = ParameterDirection.Output;
        command.Parameters.Add(id);
        await command.ExecuteNonQueryAsync(ct);
        return long.Parse(Convert.ToString(id.Value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var name = settings.Value.ConnectionName;
        DbConnection connection;
        try { connection = connections.CreateConnection(name); }
        catch (InvalidOperationException error)
        {
            throw new AttachmentStoreException($"The attachments connection {name} is not registered. Register it through AddAppBlocks, or leave Attachments:Connection empty to use AppBlocksDb.", error, AttachmentStoreProblem.NotRegistered);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A connection string the provider refuses to parse, or a folder for its file that cannot be made.
            throw new AttachmentStoreException($"The attachments connection {name} cannot be used: {error.Message}", error, AttachmentStoreProblem.Unavailable);
        }
        try
        {
            await connection.OpenAsync(ct);
            // The two tables are created in whichever database the connection opens, once per process, or found there already made by hand.
            await ApplicationDatabase.EnsureOrAcceptAsync(connection, Schema, Tables, ct);
            return connection;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await connection.DisposeAsync();
            if (error is AttachmentStoreException) throw;
            throw new AttachmentStoreException($"The attachments database through connection {name} is not available: {error.Message}", error,
                DatabaseInspector.Refusal(error) == DatabaseRefusal.None ? AttachmentStoreProblem.Unavailable : AttachmentStoreProblem.NotSetUp);
        }
    }

    /// <summary>Runs one store operation, so a refusal in its SQL reads like one on opening, and keeps the log to one line per problem.</summary>
    private async Task<T> GuardAsync<T>(bool write, Func<Task<T>> work)
    {
        var name = settings.Value.ConnectionName;
        try
        {
            var result = await work();
            // A read says nothing about a refused write, so only a write clears that one.
            if (Reported.TryGetValue(name, out var known) && (write || known != AttachmentStoreProblem.WritesRefused) && Reported.TryRemove(name, out _))
                logger.LogInformation("File uploads work again on connection {Connection}.", name);
            return result;
        }
        catch (AttachmentStoreException error) { throw Logged(error, name); }
        catch (DbException error)
        {
            var refusal = DatabaseInspector.Refusal(error);
            throw Logged(new AttachmentStoreException($"The attachments database through connection {name} refused the store: {error.Message}", error,
                refusal == DatabaseRefusal.None ? AttachmentStoreProblem.Failed : write && refusal == DatabaseRefusal.Denied ? AttachmentStoreProblem.WritesRefused : AttachmentStoreProblem.NotSetUp), name);
        }
    }

    private AttachmentStoreException Logged(AttachmentStoreException error, string name)
    {
        if (error.Problem == AttachmentStoreProblem.Failed) logger.LogWarning(error, "A file upload operation failed: {Problem}", error.Message);
        else if (!Reported.TryGetValue(name, out var known) || known != error.Problem)
        {
            Reported[name] = error.Problem;
            logger.LogWarning(error, "File uploads are off: {Problem} {Remedy} Forms and everything else keep working, and uploads return without a restart once this is done.", error.Message, error.Problem switch {
                AttachmentStoreProblem.Unsupported => "Set Attachments:Connection to AttachmentsDb, or to another SQLite, SQL Server, PostgreSQL or Oracle 12c connection.",
                AttachmentStoreProblem.NotRegistered => "Register that connection, or leave Attachments:Connection empty.",
                AttachmentStoreProblem.Unavailable => "Check the connection in Settings > AppBlocks > Connections.",
                _ => "Grant the permission or run the script shown in Settings > AppBlocks > Database setup.",
            });
        }
        return error;
    }

    /// <summary>SQL Server stops a recursive query at 100 levels unless told otherwise; a file replaced more often than that is still one chain.</summary>
    private static string Recursion(DbConnection connection) => DatabaseKinds.Of(connection) == DatabaseDialect.SqlServer ? "\nOPTION (MAXRECURSION 0)" : "";

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var kind = DatabaseKinds.Of(connection);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql(kind, sql);
        // Oracle binds by position unless told otherwise, and the store's SQL uses some parameters twice.
        if (kind == DatabaseDialect.Oracle) AppBlocks.DataSources.OracleDialect.Instance.ConfigureCommand(command);
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = kind == DatabaseDialect.Oracle ? name.TrimStart('@') : name;
            parameter.Value = value ?? DBNull.Value;
            // SQL Server needs to be told a blob is varbinary(max) and a time is datetime2, and Oracle that a blob is a BLOB rather than a RAW,
            // which holds 2000 bytes. SQLite and PostgreSQL bind by the value's type: a blob, and for PostgreSQL a UTC time as timestamptz.
            if (kind == DatabaseDialect.SqlServer && value is byte[]) { parameter.DbType = DbType.Binary; parameter.Size = -1; }
            else if (kind == DatabaseDialect.SqlServer && value is DateTime) parameter.DbType = DbType.DateTime2;
            else if (kind == DatabaseDialect.Oracle && value is byte[] && parameter.GetType().GetProperty("OracleDbType") is { CanWrite: true } type && type.PropertyType.IsEnum)
                type.SetValue(parameter, Enum.Parse(type.PropertyType, "Blob"));
            else if (kind == DatabaseDialect.Oracle && value is DateTime) parameter.DbType = DbType.DateTime;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    /// <summary>
    /// The store's SQL, written once with <c>@name</c> parameters and unquoted names, as <paramref name="kind"/> runs it. PostgreSQL gets
    /// the two table names quoted, because its script creates them in capitals as the setup screen and its grants name them, and
    /// <c>WITH RECURSIVE</c> for the revision chains. Oracle gets <c>:name</c> parameters, and <c>SIZE</c>, a reserved word there,
    /// quoted as its script creates it. Neither database has been run against this store; the SQL follows each one's documentation.
    /// </summary>
    internal static string Sql(DatabaseDialect kind, string sql) => kind switch {
        DatabaseDialect.Postgres => TableNames().Replace(sql, "\"$0\"").Replace("WITH chain (", "WITH RECURSIVE chain (", StringComparison.Ordinal),
        DatabaseDialect.Oracle or DatabaseDialect.Oracle11g => SizeColumn().Replace(Parameters().Replace(sql, ":$1"), "\"SIZE\""),
        _ => sql,
    };

    [GeneratedRegex(@"\bAPP_ATTACHMENT(_LINK)?\b")]
    private static partial Regex TableNames();

    [GeneratedRegex(@"@(\w+)")]
    private static partial Regex Parameters();

    [GeneratedRegex(@"\bSIZE\b")]
    private static partial Regex SizeColumn();

    private static string? Null(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The attachments database cannot be used: the connection is missing or its database refuses the store.</summary>
public sealed class AttachmentStoreException(string message, Exception? inner = null, AttachmentStoreProblem problem = AttachmentStoreProblem.Failed) : Exception(message, inner)
{
    public AttachmentStoreProblem Problem { get; } = problem;

    /// <summary>Whether this needs an administrator, rather than being a failure that can pass.</summary>
    public bool NeedsSetup => Problem is AttachmentStoreProblem.NotSetUp or AttachmentStoreProblem.WritesRefused or AttachmentStoreProblem.Unsupported or AttachmentStoreProblem.NotRegistered;
}

/// <summary>Why the attachments database cannot be used.</summary>
public enum AttachmentStoreProblem
{
    /// <summary>One operation failed for a reason that says nothing about the setup, such as a deadlock.</summary>
    Failed,
    /// <summary>The connection does not open.</summary>
    Unavailable,
    /// <summary>The connection names nothing registered.</summary>
    NotRegistered,
    /// <summary>The tables are missing and cannot be created, or cannot be read.</summary>
    NotSetUp,
    /// <summary>The tables can be read but this account may not write them.</summary>
    WritesRefused,
    /// <summary>The connection is a database type uploads do not run on.</summary>
    Unsupported,
}
