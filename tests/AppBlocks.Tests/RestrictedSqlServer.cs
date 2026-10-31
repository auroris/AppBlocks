using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace AppBlocks.Tests;

/// <summary>
/// A throwaway LocalDB database and a real SQL Server login for it that holds nothing but membership of <c>public</c>:
/// no DDL, and no rights on any table it is not granted. LocalDB runs in mixed mode and the Windows account is
/// sysadmin there, so the Windows account plays the database administrator through <see cref="Admin"/>, and the
/// application's connection signs in as this login, which is what makes a refusal the server's own. The reports suite links this file.
/// </summary>
internal sealed class RestrictedSqlServer : IAsyncDisposable
{
    private const string Server = @"(localdb)\MSSQLLocalDB";
    private const string Master = $"Server={Server};Integrated Security=true;Connect Timeout=90;TrustServerCertificate=true;Pooling=false";

    private RestrictedSqlServer(string database, string account, string password)
    {
        Database = database;
        Account = account;
        // Pooled, as a site's connections are: permissions must take effect on connections that are already open.
        AccountConnection = $"Server={Server};Initial Catalog={database};User Id={account};Password={password};Connect Timeout=90;TrustServerCertificate=true";
        Password = password;
    }

    public string Database { get; }
    public string Account { get; }
    public string Password { get; }
    public string AccountConnection { get; }
    public string AdminConnection => Master + $";Initial Catalog={Database}";

    public static async Task<RestrictedSqlServer> CreateAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        // Generated for this run and dropped with the database.
        var server = new RestrictedSqlServer("setup_test_" + suffix, "setup_low_" + suffix, "Low-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + "!a");
        await Execute(Master, $"CREATE DATABASE [{server.Database}]; CREATE LOGIN [{server.Account}] WITH PASSWORD = N'{server.Password}', CHECK_POLICY = OFF;");
        await server.Admin($"CREATE USER [{server.Account}] FOR LOGIN [{server.Account}];");
        return server;
    }

    /// <summary>Runs a script as the database administrator, batch by batch at its <c>GO</c> lines, as sqlcmd would.</summary>
    public async Task Admin(string script)
    {
        foreach (var batch in Batches(script)) await Execute(AdminConnection, batch);
    }

    /// <summary>A single value, read as the database administrator: what is really in the database, whatever the login may see.</summary>
    public async Task<T> AdminScalar<T>(string sql)
    {
        await using var connection = new SqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    /// <summary>A single value, read as the login.</summary>
    public async Task<T> AccountScalar<T>(string sql)
    {
        await using var connection = new SqlConnection(AccountConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    public Task<int> TableCount() => AdminScalar<int>("SELECT COUNT(*) FROM sys.tables");

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await Execute(Master, $"ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Database}]; DROP LOGIN [{Account}];");
    }

    internal static IEnumerable<string> Batches(string script)
    {
        var batch = new List<string>();
        foreach (var line in script.Split('\n'))
        {
            if (!line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase)) { batch.Add(line); continue; }
            if (batch.Any(item => !string.IsNullOrWhiteSpace(item))) yield return string.Join('\n', batch);
            batch.Clear();
        }
        if (batch.Any(item => !string.IsNullOrWhiteSpace(item))) yield return string.Join('\n', batch);
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
