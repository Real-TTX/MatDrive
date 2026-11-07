using MatDrive.Data;
using MatDrive.Services.Files;

namespace MatDrive.Pages.Drive;

/// <summary>The space of My Drive: how much is used of how much, what takes the space (the largest files, the trash, older versions), and the shared drives.</summary>
public sealed class StorageModel(AppDbContext db) : AppPageModel
{
    public DriveUsage Usage { get; private set; } = new(0, null);

    public IReadOnlyList<NodeRow> Largest { get; private set; } = [];

    public long TrashBytes { get; private set; }

    public long OldVersionBytes { get; private set; }

    public IReadOnlyList<DriveRow> SharedDrives { get; private set; } = [];

    public string Zone => Me.TimeZone;

    public async Task OnGetAsync()
    {
        var mine = await DriveService.MyDriveAsync(db, UserId);
        Usage = await DriveService.UsageAsync(db, mine);
        Largest = await NodeInfo.LargestAsync(db, UserId, mine.Id, 25) ?? [];
        (TrashBytes, OldVersionBytes) = await NodeInfo.SpareBytesAsync(db, mine.Id);
        SharedDrives = await DriveService.SharedDrivesAsync(db, UserId);
        ViewData["NavKey"] = "my";
    }
}
