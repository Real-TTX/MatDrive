using MatDrive.Services.Office;

namespace MatDrive.Tests;

/// <summary>Which files open in MatOffice with a click, and which open in the preview first (they still offer "Open with MatOffice").</summary>
public sealed class OfficeFormatTests
{
    // what MatOffice answers in /api/v1/host/formats (its own format is listed too)
    private static readonly IReadOnlyList<OfficeKind> Kinds =
    [
        new("docs", "Docs", "#185abd", ".matdoc", ".docx", [Open(".docx"), Open(".html"), Open(".md"), Open(".txt"), Open(".matdoc")]),
        new("sheets", "Sheets", "#107c41", ".matsheet", ".xlsx", [Open(".xlsx"), Open(".csv"), Open(".tsv"), Open(".matsheet")]),
        new("base", "Base", "#a4373a", ".matbase", ".matbase", [Open(".xlsx"), Open(".matbase")]),
    ];

    private static OfficeFormat Open(string ext) => new(ext, ext, Open: true, Write: true);

    [Theory]
    [InlineData("Report.docx", true)]
    [InlineData("Budget.XLSX", true)]
    [InlineData("Plan.matsheet", true)]
    [InlineData("Customers.matbase", true)]
    [InlineData("notes.txt", false)]
    [InlineData("README.md", false)]
    [InlineData("page.html", false)]
    [InlineData("data.csv", false)]
    [InlineData("photo.png", false)]
    public void A_click_opens_office_formats_in_MatOffice_and_other_readable_files_in_the_preview(string name, bool byDefault)
        => Assert.Equal(byDefault, OfficeService.OpensByDefault(Kinds, name));

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("data.csv")]
    [InlineData("Report.docx")]
    public void Every_file_MatOffice_reads_can_still_be_opened_there(string name)
        => Assert.NotNull(OfficeService.FormatOf(Kinds, name));

    [Fact]
    public void Without_MatOffice_nothing_opens_there()
        => Assert.False(OfficeService.OpensByDefault([], "Report.docx"));
}
