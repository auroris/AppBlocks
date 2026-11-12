using System.Security.Claims;
using AppBlocks.Attachments;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace AppBlocks.Tests;

public sealed class AttachmentCleanupJobTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(6)]
    public async Task Registered_hourly_job_deletes_only_expired_pending_files(int? configuredHours)
    {
        var root = Directory.CreateTempSubdirectory("attachment-cleanup-").FullName;
        var file = Path.Combine(root, "files.db");
        var connectionString = $"Data Source={file};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await using var provider = Provider("FilesDb", connectionString, configuredHours);
            var job = Job(provider);
            Assert.Equal(TimeSpan.FromHours(1), job.Period);

            await using var scope = provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<AttachmentStore>();
            var caller = new ClaimsPrincipal(new ClaimsIdentity());
            AttachmentOwner Pending() => AttachmentOwner.Pending("orders", Guid.NewGuid(), caller);
            var expired = Pending(); var recent = Pending(); var adopted = Pending(); var removed = Pending();
            var owner = new AttachmentOwner("orders", "7");
            async Task<AttachmentItem> Add(AttachmentOwner target) => await store.AddAsync(target,
                new("file.txt", "text/plain", [1, 2, 3]), null, null, null, "anonymous", ct);
            await Add(expired); await Add(expired);
            var kept = await Add(recent);
            var associated = await Add(adopted);
            var deleted = await Add(removed);
            await store.RemoveAsync(removed, deleted.Id, "anonymous", ct);
            await store.AdoptAsync(adopted, owner, "anonymous", ct);

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            await using (var age = connection.CreateCommand())
            {
                age.CommandText = "UPDATE APP_ATTACHMENT_LINK SET CREATED_AT = @old WHERE OWNER_KEY <> @recent";
                age.Parameters.AddWithValue("@old", DateTime.UtcNow.AddHours(-(configuredHours ?? 24) - 1));
                age.Parameters.AddWithValue("@recent", recent.Key);
                await age.ExecuteNonQueryAsync(ct);
            }
            // No HTTP request triggers cleanup; the registered job creates its own service scope.
            await job.RunJobAsync(ct);
            Assert.Empty(await store.ListAsync(expired, ct));
            Assert.Equal(kept.Id, Assert.Single(await store.ListAsync(recent, ct)).Id);
            Assert.Equal(associated.Id, Assert.Single(await store.ListAsync(owner, ct)).Id);
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM APP_ATTACHMENT";
            Assert.Equal(2L, await count.ExecuteScalarAsync(ct));
            count.CommandText = "SELECT COUNT(*) FROM APP_ATTACHMENT_LINK";
            Assert.Equal(2L, await count.ExecuteScalarAsync(ct));
            await job.RunJobAsync(ct);
            Assert.Equal(2L, await count.ExecuteScalarAsync(ct));
        }
        finally { File.Delete(file); Directory.Delete(root); }
    }

    [Fact]
    public async Task The_job_leaves_a_site_that_never_stored_a_file_as_it_is()
    {
        var root = Directory.CreateTempSubdirectory("attachment-cleanup-").FullName;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            // No database file, and then a database without the upload tables: the job creates neither.
            var file = Path.Combine(root, "files.db");
            var connectionString = $"Data Source={file};Pooling=False";
            await using var provider = Provider("UnusedFilesDb", connectionString);
            await Job(provider).RunJobAsync(ct);
            Assert.False(File.Exists(file));

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(ct);
                await using var create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE ORDERS (ID INTEGER PRIMARY KEY)";
                await create.ExecuteNonQueryAsync(ct);
            }
            await Job(provider).RunJobAsync(ct);
            await using var check = new SqliteConnection(connectionString);
            await check.OpenAsync(ct);
            await using var tables = check.CreateCommand();
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
            await using var reader = await tables.ExecuteReaderAsync(ct);
            var names = new List<string>();
            while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
            Assert.Equal(["ORDERS"], names);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_store_that_fails_leaves_the_job_to_try_again_next_hour()
    {
        var root = Directory.CreateTempSubdirectory("attachment-cleanup-").FullName;
        var connectionString = $"Data Source={Path.Combine(root, "files.db")};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        try
        {
            // Tables of the right names made by hand without the store's columns: the cleanup's query fails.
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(ct);
                await using var create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE APP_ATTACHMENT (X INTEGER); CREATE TABLE APP_ATTACHMENT_LINK (X INTEGER);";
                await create.ExecuteNonQueryAsync(ct);
            }
            await using var provider = Provider("BrokenFilesDb", connectionString);
            await using (var scope = provider.CreateAsyncScope())
                await Assert.ThrowsAsync<AttachmentStoreException>(() => scope.ServiceProvider.GetRequiredService<AttachmentStore>().ExpirePendingAsync(ct));
            await Job(provider).RunJobAsync(ct);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ServiceProvider Provider(string name, string connectionString, int? pendingLifetimeHours = null)
    {
        var values = new Dictionary<string, string?> { ["Attachments:Connection"] = name };
        if (pendingLifetimeHours is { } hours) values["Attachments:PendingLifetimeHours"] = hours.ToString();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddTestConnections(connections => connections.AddConnection(name, _ => new SqliteConnection(connectionString)));
        var builder = TestStubs.Stub<IUmbracoBuilder>((method, _) => method.Name == "get_Services"
            ? services : throw new NotImplementedException(method.Name));
        builder.AddAttachments(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static AttachmentCleanupJob Job(IServiceProvider provider)
        => Assert.IsType<AttachmentCleanupJob>(Assert.Single(provider.GetServices<IRecurringBackgroundJob>()));
}
