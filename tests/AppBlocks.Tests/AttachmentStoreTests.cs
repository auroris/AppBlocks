using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AppBlocks.Attachments;

namespace AppBlocks.Tests;

/// <summary>The attachment tables and the store over them, on SQLite and on SQL Server Express LocalDB where it is installed.</summary>
public sealed class AttachmentStoreTests
{
    private static readonly AttachmentOwner Order = new("sample.orders", "4");
    private static readonly AttachmentOwner Other = new("sample.orders", "5");

    [Fact]
    public async Task Files_are_filed_renamed_removed_and_purged_per_owner_on_sqlite()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("attachments-").FullName, "attachments.db");
        await Exercise(new SqliteConnection($"Data Source={file};Pooling=False").ConnectionString, text => new SqliteConnection(text));
    }

    [LocalDbFact]
    public async Task Files_are_filed_renamed_removed_and_purged_per_owner_on_sql_server()
    {
        const string master = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Connect Timeout=90;TrustServerCertificate=true";
        var database = "att_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var connection = new SqlConnection(master)) { await connection.OpenAsync(); await using var create = connection.CreateCommand(); create.CommandText = $"CREATE DATABASE [{database}]"; await create.ExecuteNonQueryAsync(); }
        try { await Exercise(master + $";Initial Catalog={database};Pooling=false", text => new SqlConnection(text)); }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var connection = new SqlConnection(master); await connection.OpenAsync();
            await using var drop = connection.CreateCommand(); drop.CommandText = $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]"; await drop.ExecuteNonQueryAsync();
        }
    }

    internal static async Task Exercise(string connectionString, Func<string, DbConnection> open)
    {
        var store = Store(connectionString, open);
        Assert.Empty(await store.ListAsync(Order, default));

        var invoice = await store.AddAsync(Order, Upload("invoice.pdf", "application/pdf", "first invoice"), "Invoice", "October", "Paid by card", "member:1", default);
        var note = await store.AddAsync(Order, Upload("note.txt", "text/plain", "a note"), null, null, null, "member:1", default);
        await store.AddAsync(Other, Upload("other.pdf", "application/pdf", "someone else's"), "Invoice", null, null, "member:2", default);
        Assert.Equal("invoice.pdf", invoice.FileName);
        Assert.Equal(Sha("first invoice"), invoice.Sha256);
        Assert.Equal(("Invoice", "October", "Paid by card"), (invoice.Purpose, invoice.Label, invoice.Notes));
        var listed = await store.ListAsync(Order, default);
        Assert.Equal([note.Id, invoice.Id], listed.Select(i => i.Id));  // newest first

        // The bytes come back for the owner, and never for another owner's key or another owner's file.
        var read = await store.ReadAsync(Order, invoice.Id, default);
        Assert.Equal("first invoice", Encoding.UTF8.GetString(read!.Bytes));
        Assert.Equal("application/pdf", read.ContentType);
        Assert.Null(await store.ReadAsync(Other, invoice.Id, default));
        var foreign = (await store.ListAsync(Other, default)).Single();
        Assert.Null(await store.ReadAsync(Order, foreign.Id, default));
        Assert.False(await store.RemoveAsync(Order, foreign.Id, "member:1", default));

        // A rename changes only the filename and link's update details, under the same owner check.
        Assert.Null(await store.RenameAsync(Order, foreign.Id, "not-mine.pdf", "member:1", default));
        var renamed = await store.RenameAsync(Order, invoice.Id, "invoice-renamed.pdf", "member:3", default);
        Assert.NotNull(renamed);
        Assert.Equal(invoice.Id, renamed.Id);
        Assert.Equal("invoice-renamed.pdf", renamed.FileName);
        Assert.Equal((invoice.Size, invoice.Sha256, invoice.Purpose, invoice.Label, invoice.Notes, invoice.UploadedBy, invoice.UploadedAt),
            (renamed.Size, renamed.Sha256, renamed.Purpose, renamed.Label, renamed.Notes, renamed.UploadedBy, renamed.UploadedAt));
        Assert.Equal("member:3", renamed.UpdatedBy);
        var renamedContent = await store.ReadAsync(Order, invoice.Id, default);
        Assert.Equal("invoice-renamed.pdf", renamedContent!.FileName);
        Assert.Equal("first invoice", Encoding.UTF8.GetString(renamedContent.Bytes));

        // A new version of a document is simply another file; nothing ties it to the first.
        var second = await store.AddAsync(Order, Upload("invoice-v2.pdf", "application/pdf", "second invoice"), "Invoice", "October (corrected)", null, "member:3", default);
        Assert.Equal([second.Id, note.Id, invoice.Id], (await store.ListAsync(Order, default)).Select(i => i.Id));
        Assert.Equal("first invoice", Encoding.UTF8.GetString((await store.ReadAsync(Order, invoice.Id, default))!.Bytes));

        // Removal hides the link; the rows stay until a purge past the retention, which deletes the link and its file.
        Assert.True(await store.RemoveAsync(Order, invoice.Id, "member:1", default));
        Assert.Equal([second.Id, note.Id], (await store.ListAsync(Order, default)).Select(i => i.Id));
        Assert.Null(await store.ReadAsync(Order, invoice.Id, default));
        Assert.Null(await store.RenameAsync(Order, invoice.Id, "removed.pdf", "member:1", default));
        Assert.False(await store.RemoveAsync(Order, invoice.Id, "member:1", default));
        Assert.Equal(4L, await Count(connectionString, open, "SELECT COUNT(*) FROM APP_ATTACHMENT"));
        var kept = await store.PurgeAsync(DateTime.UtcNow.AddDays(-1), default);
        Assert.Equal(new AttachmentPurge(0, 0, 0), kept);
        var purged = await store.PurgeAsync(DateTime.UtcNow.AddMinutes(1), default);
        Assert.Equal(new AttachmentPurge(1, 1, "first invoice".Length), purged);
        Assert.Equal(3L, await Count(connectionString, open, "SELECT COUNT(*) FROM APP_ATTACHMENT"));
        Assert.Equal([second.Id, note.Id], (await store.ListAsync(Order, default)).Select(i => i.Id));
        Assert.Equal(foreign.Id, (await store.ListAsync(Other, default)).Single().Id);

        // The schema scripts are idempotent: a second store over the same database finds everything in place.
        Assert.Equal(2, (await Store(connectionString, open).ListAsync(Order, default)).Count);
    }

    [Fact]
    public async Task Pending_files_belong_to_their_uploader_move_to_the_record_and_expire_on_sqlite()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("attachments-pending-").FullName, "attachments.db");
        var connectionString = new SqliteConnection($"Data Source={file};Pooling=False").ConnectionString;
        static System.Security.Claims.ClaimsPrincipal Member(string id) => new(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier, id)], "Test"));
        var submission = Guid.NewGuid();
        var mine = AttachmentOwner.Pending("sample.orders", submission, Member("7"));
        var theirs = AttachmentOwner.Pending("sample.orders", submission, Member("8"));
        var anonymous = AttachmentOwner.Pending("sample.orders", submission, new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()));
        Assert.True(mine.IsPending);
        Assert.False(Order.IsPending);
        Assert.Equal("sample.orders", mine.ResourceAlias);
        Assert.Equal(submission + ":7", mine.Key);
        Assert.Equal(submission + ":", anonymous.Key);
        Assert.NotEqual(mine, theirs);

        var store = Store(connectionString, text => new SqliteConnection(text));
        var draft = await store.AddAsync(mine, Upload("draft.pdf", "application/pdf", "draft"), null, null, null, "member:7", default);
        Assert.Empty(await store.ListAsync(theirs, default));
        Assert.Null(await store.ReadAsync(theirs, draft.Id, default));
        Assert.True(await store.PendingForOthersAsync(theirs, submission, default));
        Assert.False(await store.PendingForOthersAsync(mine, submission, default));
        Assert.False(await store.PendingForOthersAsync(mine, Guid.NewGuid(), default));

        // Only the uploader's own pending files move, and they keep their ids.
        Assert.Equal(0, await store.AdoptAsync(theirs, Order, "member:8", default));
        Assert.Equal(1, await store.AdoptAsync(mine, Order, "member:7", default));
        Assert.Empty(await store.ListAsync(mine, default));
        Assert.Equal([draft.Id], (await store.ListAsync(Order, default)).Select(i => i.Id));

        // Files left pending past the lifetime go on the next pending operation; adopted files stay.
        var late = AttachmentOwner.Pending("sample.orders", Guid.NewGuid(), Member("7"));
        await store.AddAsync(late, Upload("late.pdf", "application/pdf", "late"), null, null, null, "member:7", default);
        Assert.Equal(0, await store.ExpirePendingAsync(default));
        Assert.Equal(1, await Store(connectionString, text => new SqliteConnection(text), pendingLifetimeHours: 0).ExpirePendingAsync(default));
        Assert.Empty(await store.ListAsync(late, default));
        Assert.Equal(1L, await Count(connectionString, text => new SqliteConnection(text), "SELECT COUNT(*) FROM APP_ATTACHMENT"));
        Assert.Equal([draft.Id], (await store.ListAsync(Order, default)).Select(i => i.Id));
    }

    [Fact]
    public async Task Adoption_can_commit_or_rollback_with_the_callers_transaction()
    {
        var root = Directory.CreateTempSubdirectory("attachment-transaction-").FullName;
        var file = Path.Combine(root, "files.db");
        var connectionString = $"Data Source={file};Pooling=False";
        try
        {
            var store = Store(connectionString, value => new SqliteConnection(value));
            var caller = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier, "7")], "Test"));
            var pending = AttachmentOwner.Pending(Order.Alias, Guid.NewGuid(), caller);
            var fileItem = await store.AddAsync(pending, Upload("pending.pdf", "application/pdf", "pending"), null, null, null, "7", TestContext.Current.CancellationToken);
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using (var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                Assert.Equal(1, await AttachmentStore.AdoptAsync(connection, transaction, pending, Order, "7", TestContext.Current.CancellationToken));
                await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            }
            Assert.Equal(fileItem.Id, Assert.Single(await store.ListAsync(pending, TestContext.Current.CancellationToken)).Id);
            Assert.Empty(await store.ListAsync(Order, TestContext.Current.CancellationToken));
            await using (var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                Assert.Equal(1, await AttachmentStore.AdoptAsync(connection, transaction, pending, Order, "7", TestContext.Current.CancellationToken));
                await transaction.CommitAsync(TestContext.Current.CancellationToken);
            }
            Assert.Equal(fileItem.Id, Assert.Single(await store.ListAsync(Order, TestContext.Current.CancellationToken)).Id);
        }
        finally { File.Delete(file); Directory.Delete(root); }
    }

    private static AttachmentStore Store(string connectionString, Func<string, DbConnection> open, int pendingLifetimeHours = 24)
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddTestConnections(connections => connections.AddConnection("FilesDb", _ => open(connectionString)));
        var provider = services.BuildServiceProvider();
        return new AttachmentStore(provider.GetRequiredService<AppBlocks.Connections.IApplicationConnectionFactory>(),
            Options.Create(new AttachmentSettings { Connection = "FilesDb", PendingLifetimeHours = pendingLifetimeHours }), NullLogger<AttachmentStore>.Instance);
    }

    private static AttachmentUpload Upload(string name, string type, string text) => new(name, type, Encoding.UTF8.GetBytes(text));

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static async Task<long> Count(string connectionString, Func<string, DbConnection> open, string sql)
    {
        await using var connection = open(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
