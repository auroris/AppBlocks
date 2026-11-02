using AppBlocks.Code;

namespace AppBlocks.Tests;

/// <summary>The C# folder takes only paths that stay below its root, and only .cs files with plain names.</summary>
public sealed class CodeFolderTests : IDisposable
{
    private readonly string root = Path.Combine(Directory.CreateTempSubdirectory("code-folder-").FullName, "AppBlocks", "Code");
    private CodeFolder Folder => new(root);

    public void Dispose()
    {
        // A junction goes first, on its own, so the recursive delete never reaches through it.
        try { if (Directory.Exists(Path.Combine(root, "Linked"))) Directory.Delete(Path.Combine(root, "Linked")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(root))!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("../Escape.cs")]
    [InlineData("Orders/../../Escape.cs")]
    [InlineData("/Rooted.cs")]
    [InlineData("C:/Windows/Rooted.cs")]
    [InlineData("C:Drive.cs")]
    [InlineData("\\\\server\\share\\Unc.cs")]
    [InlineData("Orders//Order.cs")]
    [InlineData("./Order.cs")]
    [InlineData("Orders/")]
    [InlineData(".hidden.cs")]
    [InlineData("With space.cs")]
    [InlineData("Order.cs.")]
    [InlineData("CON.cs")]
    [InlineData("nul.cs")]
    [InlineData("Order.txt")]
    [InlineData("Order.CS")]
    [InlineData("")]
    public void A_file_path_outside_the_rules_is_refused(string path)
        => Assert.Throws<CodePathException>(() => Folder.Resolve(path, file: true));

    [Fact]
    public void A_path_inside_the_root_resolves_with_either_separator()
    {
        Assert.Equal(Path.Combine(root, "Orders", "OrderForm.cs"), Folder.Resolve("Orders\\OrderForm.cs", file: true));
        Assert.Equal("Orders/Lines/Line_1.cs", CodeFolder.Normalize(" Orders/Lines/Line_1.cs ", file: true));
        Assert.Throws<CodePathException>(() => CodeFolder.Normalize("Orders.cs", file: false));
        Assert.Throws<CodePathException>(() => CodeFolder.Normalize(string.Join('/', Enumerable.Repeat("a", CodeFolder.MaxDepth + 1)) + ".cs", file: true));
        Assert.Throws<CodePathException>(() => CodeFolder.Normalize(new string('a', CodeFolder.MaxNameLength) + ".cs", file: true));
    }

    [Fact]
    public async Task The_first_write_creates_the_folder_and_nothing_before_it_does()
    {
        var folder = Folder;
        Assert.False(folder.Exists);
        Assert.Empty(folder.List());
        await Assert.ThrowsAsync<FileNotFoundException>(() => folder.ReadAsync("Missing.cs", default));
        Assert.False(folder.Exists);

        await folder.WriteAsync("Orders/OrderForm.cs", "class OrderForm {}", create: true, default);
        Assert.True(folder.Exists);
        await Assert.ThrowsAsync<CodePathException>(() => folder.WriteAsync("Orders/OrderForm.cs", "", create: true, default));
        await Assert.ThrowsAsync<FileNotFoundException>(() => folder.WriteAsync("Orders/Other.cs", "", create: false, default));
        await Assert.ThrowsAsync<CodePathException>(() => folder.WriteAsync("Big.cs", new string('x', CodeFolder.MaxFileBytes + 1), create: true, default));
        File.WriteAllText(Path.Combine(root, "notes.txt"), "not code");

        Assert.Equal(["Orders", "Orders/OrderForm.cs"], folder.List().Select(e => e.Path));
        Assert.Equal("class OrderForm {}", await folder.ReadAsync("Orders/OrderForm.cs", default));
    }

    [Fact]
    public async Task Rename_and_delete_stay_inside_the_folder()
    {
        var folder = Folder;
        await folder.WriteAsync("Orders/OrderForm.cs", "class OrderForm {}", create: true, default);
        folder.CreateFolder("Archive");
        Assert.Throws<CodePathException>(() => folder.CreateFolder("Archive"));

        folder.Rename("Orders/OrderForm.cs", "Orders/Form.cs");
        folder.Rename("Orders", "Archive/Orders");
        Assert.Throws<CodePathException>(() => folder.Rename("Archive", "Archive/Inner"));
        Assert.Throws<CodePathException>(() => folder.Rename("Archive/Orders/Form.cs", "../Form.cs"));
        Assert.Throws<CodePathException>(() => folder.Rename("Archive/Orders/Form.cs", "Archive/Orders/Form.txt"));
        Assert.Equal(["Archive", "Archive/Orders", "Archive/Orders/Form.cs"], folder.List().Select(e => e.Path));

        Assert.Throws<CodePathException>(() => folder.Delete(".."));
        folder.Delete("Archive/Orders/Form.cs");
        folder.Delete("Archive");
        Assert.Empty(folder.List());
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public async Task A_link_below_the_root_is_neither_followed_nor_listed()
    {
        var outside = Directory.CreateTempSubdirectory("code-outside-").FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "Secret.cs"), "class Secret {}");
        Directory.CreateDirectory(root);
        var link = Path.Combine(root, "Linked");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A Windows junction needs no privilege, and is a link the folder must refuse just the same.
            if (!OperatingSystem.IsWindows()) Assert.Skip("This account cannot create symbolic links: " + error.Message);
            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, outside]) { RedirectStandardOutput = true, UseShellExecute = false })!;
            await mklink.WaitForExitAsync();
            if (!Directory.Exists(link)) Assert.Skip("This account can create neither a symbolic link nor a junction: " + error.Message);
        }
        var folder = Folder;
        Assert.Throws<CodePathException>(() => folder.Resolve("Linked/Secret.cs", file: true));
        await Assert.ThrowsAsync<CodePathException>(() => folder.ReadAsync("Linked/Secret.cs", default));
        await Assert.ThrowsAsync<CodePathException>(() => folder.WriteAsync("Linked/New.cs", "", create: true, default));
        Assert.DoesNotContain(folder.List(), entry => entry.Path.StartsWith("Linked", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(outside, "New.cs")));
    }
}
