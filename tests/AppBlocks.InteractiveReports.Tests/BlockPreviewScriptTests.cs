using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AppBlocks.InteractiveReports.Tests;

/// <summary>
/// The backoffice report preview's column reading, run in Node. The preview module imports the backoffice, so only its
/// self-contained <c>startingColumns</c> function is taken from the shipped file.
/// </summary>
public sealed partial class BlockPreviewScriptTests
{
    [GeneratedRegex(@"^export function startingColumns\(state\) \{.*?^\}", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex StartingColumns();

    [Fact]
    public async Task The_preview_shows_the_columns_of_the_starting_views_active_table()
    {
        var states = new object?[] {
            // The active table's last selection wins.
            new { activeTable = "rows", tables = new Dictionary<string, object> {
                ["base"] = new { from = "definition", composables = new object[] { new { kind = "select", columns = new[] { "A" } } } },
                ["rows"] = new { from = "base", composables = new object[] {
                    new { kind = "select", columns = new[] { "NAME", "EMAIL" } }, new { kind = "sort", sorts = Array.Empty<object>() },
                    new { kind = "select", columns = new[] { "NAME", "PHONE" } } } },
            } },
            // A table that selects nothing shows what the table it is built from selects.
            new { activeTable = "filtered", tables = new Dictionary<string, object> {
                ["base"] = new { from = "definition", composables = new object[] { new { kind = "select", columns = new[] { "ID", "TITLE" } } } },
                ["filtered"] = new { from = "base", composables = new object[] { new { kind = "filter" } } },
            } },
            // No active table named: the first table.
            new { tables = new Dictionary<string, object> { ["base"] = new { composables = new object[] { new { kind = "select", columns = new[] { "X" } } } } } },
            // A report document without selections, or none at all, shows none.
            new { activeTable = "base", tables = new Dictionary<string, object> { ["base"] = new { composables = Array.Empty<object>() } } },
            null,
        };
        var results = await RunAsync(states);
        Assert.Equal([["NAME", "PHONE"], ["ID", "TITLE"], ["X"], [], []], results);
    }

    private static async Task<string[][]> RunAsync(object?[] states, [CallerFilePath] string source = "")
    {
        var script = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!,
            "../../src/AppBlocks.InteractiveReports/wwwroot/App_Plugins/AppBlocks.InteractiveReports/block-preview.js"));
        var function = StartingColumns().Match(await File.ReadAllTextAsync(script));
        Assert.True(function.Success, "block-preview.js no longer exports startingColumns(state).");
        var program = function.Value.Replace("export ", "") + "\nconsole.log(JSON.stringify(" + JsonSerializer.Serialize(states) + ".map(startingColumns)));";
        var start = new ProcessStartInfo("node", ["--input-type=module", "-"]) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        Process node;
        try { node = Process.Start(start)!; }
        catch (System.ComponentModel.Win32Exception) { Assert.Skip("Node is not on the PATH."); throw; }
        using (node)
        {
            await node.StandardInput.WriteAsync(program);
            node.StandardInput.Close();
            var output = await node.StandardOutput.ReadToEndAsync();
            var error = await node.StandardError.ReadToEndAsync();
            await node.WaitForExitAsync();
            Assert.True(node.ExitCode == 0, error);
            return JsonSerializer.Deserialize<string[][]>(output)!;
        }
    }
}
