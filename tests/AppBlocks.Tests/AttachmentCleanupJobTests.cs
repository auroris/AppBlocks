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
            var values = new Dictionary<string, string?> { ["Attachments:Connection"] = "FilesDb" };
            if (configuredHours is { } hours) values["Attachments:PendingLifetimeHours"] = hours.ToString();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddTestConnections(connections => connections.AddConnection("FilesDb", _ => new SqliteConnection(connectionString)));
            var builder = TestStubs.Stub<IUmbracoBuilder>((method, _) => method.Name == "get_Services"
                ? services : throw new NotImplementedException(method.Name));
            builder.AddAttachments(configuration);
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var job = Assert.IsType<AttachmentCleanupJob>(Assert.Single(provider.GetServices<IRecurringBackgroundJob>()));
            Assert.Equal(TimeSpan.FromHours(1), job.Period);

            await using var scope = provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<AttachmentStore>();
            var caller = new ClaimsPrincipal(new ClaimsIdentity());
            AttachmentOwner Pending() => AttachmentOwner.Pending("orders", Guid.NewGuid(), caller);
            var expired = Pending(); var recent = Pending(); var adopted = Pending(); var removed = Pending();
            var owner = new AttachmentOwner("orders", "7");
            async Task<AttachmentItem> Add(AttachmentOwner target) => await store.AddAsync(target,
                new("file.txt", "text/plain", [1, 2, 3]), null, null, null, "anonymous", ct);
            var original = await Add(expired);
            await store.ReplaceAsync(expired, original.Id, new("replacement.txt", "text/plain", [4, 5]), null, null, null, "anonymous", ct);
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
}
