using System.Runtime.CompilerServices;

namespace AppBlocks.Tests;

/// <summary>Runs only where SQL Server Express LocalDB is installed; elsewhere the test is skipped with the reason.</summary>
public sealed class LocalDbFactAttribute : FactAttribute
{
    public LocalDbFactAttribute(
        [CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!Installed()) Skip = "SQL Server Express LocalDB is not installed, so the SQL Server data source test cannot run here.";
    }

    private static bool Installed()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var versions = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions");
        return versions?.GetSubKeyNames().Length > 0;
    }
}
