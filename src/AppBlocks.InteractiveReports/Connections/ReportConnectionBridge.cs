using System.Data.Common;
using AppBlocks.Connections;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;

namespace AppBlocks.InteractiveReports;

internal sealed class ReportConnectionBridge(IApplicationConnectionFactory application, AppBlocksBuilder builder,
    ConnectionCatalog catalog, IReportConnectionFactory reports, ReportDefinitionCompiler compiler) : IApplicationConnectionFactory
{
    private bool IsApplication(string name) => catalog.Find(name) is not null || builder.Connections.ContainsKey(name);
    public DbConnection CreateConnection(string name) => IsApplication(name) ? application.CreateConnection(name) : reports.CreateConnection(name);
    public DatabaseDialect Dialect(string name) => IsApplication(name) ? application.Dialect(name)
        : FromReportDialect(compiler.Compile(name, new ReportDefinition { Connection = name, Sql = "SELECT 1" }).Dialect
            ?? throw new InvalidOperationException($"The connection {name} has no SQL dialect."));
    public void SetDetectedDialect(string name, DatabaseDialect dialect)
    {
        application.SetDetectedDialect(name, dialect);
        reports.SetDetectedDialect(name, ToReportDialect(dialect));
    }

    /// <summary>The report engine's dialect for an AppBlocks database kind: the companion's one map between the two.</summary>
    internal static ReportDialect ToReportDialect(DatabaseDialect dialect) => dialect switch
    {
        DatabaseDialect.Sqlite => ReportDialect.Sqlite, DatabaseDialect.SqlServer => ReportDialect.SqlServer,
        DatabaseDialect.Postgres => ReportDialect.Postgres, DatabaseDialect.Oracle => ReportDialect.Oracle,
        DatabaseDialect.Oracle11g => ReportDialect.Oracle11g, _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
    };

    /// <summary>The AppBlocks database kind for a report engine dialect, read back through <see cref="ToReportDialect"/>; false for one AppBlocks has no kind for.</summary>
    internal static bool TryFromReportDialect(ReportDialect dialect, out DatabaseDialect kind)
    {
        foreach (var candidate in DatabaseKinds.All)
            if (ToReportDialect(candidate) == dialect) { kind = candidate; return true; }
        kind = default;
        return false;
    }

    private static DatabaseDialect FromReportDialect(ReportDialect dialect)
        => TryFromReportDialect(dialect, out var kind) ? kind : throw new InvalidOperationException($"Unsupported database dialect: {dialect}.");
}
