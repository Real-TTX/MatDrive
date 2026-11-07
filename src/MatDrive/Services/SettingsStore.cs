using MatDrive.Data;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services;

/// <summary>The one row of the settings of the installation (Id 1): read it, and make it the first time (two requests at the same moment both end with the same row).</summary>
public static class SettingsStore
{
    public static async Task<SystemSetting> LoadAsync(AppDbContext db, CancellationToken ct = default)
    {
        var s = await db.SystemSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (s is not null)
        {
            return s;
        }

        s = new SystemSetting { Id = 1 };
        db.SystemSettings.Add(s);
        try
        {
            await db.SaveChangesAsync(ct);
            return s;
        }
        catch (DbUpdateException)
        {
            db.Entry(s).State = EntityState.Detached;
            return await db.SystemSettings.FirstAsync(x => x.Id == 1, ct);
        }
    }
}
