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

    internal static ReportDialect ToReportDialect(DatabaseDialect dialect) => dialect switch
    {
        DatabaseDialect.Sqlite => ReportDialect.Sqlite, DatabaseDialect.SqlServer => ReportDialect.SqlServer,
        DatabaseDialect.Postgres => ReportDialect.Postgres, DatabaseDialect.Oracle => ReportDialect.Oracle,
        DatabaseDialect.Oracle11g => ReportDialect.Oracle11g, _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
    };
    private static DatabaseDialect FromReportDialect(ReportDialect dialect) => dialect switch
    {
        ReportDialect.Sqlite => DatabaseDialect.Sqlite, ReportDialect.SqlServer => DatabaseDialect.SqlServer,
        ReportDialect.Postgres => DatabaseDialect.Postgres, ReportDialect.Oracle => DatabaseDialect.Oracle,
        ReportDialect.Oracle11g => DatabaseDialect.Oracle11g, _ => throw new InvalidOperationException($"Unsupported database dialect: {dialect}."),
    };
}
