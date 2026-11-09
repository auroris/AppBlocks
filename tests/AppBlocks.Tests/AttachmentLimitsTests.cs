using AppBlocks.Attachments;

namespace AppBlocks.Tests;

public sealed class AttachmentLimitsTests
{
    [Theory]
    [InlineData("", "", "file.exe", true)]
    [InlineData("pdf", "", "file.PDF", true)]
    [InlineData("pdf", "", "file.exe", false)]
    [InlineData("", ".docx", "file.docx", true)]
    [InlineData("", ".docx", "file.exe", false)]
    [InlineData(".pdf .docx", ".docx .txt", "file.docx", true)]
    [InlineData(".pdf .docx", ".docx .txt", "file.pdf", false)]
    [InlineData(".pdf .docx", ".docx .txt", "file.txt", false)]
    [InlineData(".pdf", ".docx", "file.pdf", false)]
    [InlineData(".pdf", ".docx", "file.docx", false)]
    [InlineData(".pdf", ".docx", "file.exe", false)]
    [InlineData(".pdf", ".docx", "file", false)]
    public void A_block_accepts_only_file_types_allowed_by_both_limits(string configured, string chosen, string file, bool accepted)
    {
        var settings = new AttachmentSettings { AllowedExtensions = AttachmentLimits.Parse(configured).ToList() };
        Assert.Equal(accepted, AttachmentLimits.For(null, chosen, settings).Accepts(file));
    }
}
