using System.Text;
using MatDrive.Data;
using MatDrive.Services.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MatDrive.Tests;

/// <summary>Uploads that run at the same moment, like the parallel workers of the explorer when a whole folder is dropped.</summary>
public sealed class UploadTests
{
    [DbFact]
    public async Task Parallel_uploads_of_one_folder_make_each_of_its_folders_once()
    {
        await using var t = await TestDatabase.CreateAsync();
        var ada = await t.AddUserAsync("Ada");
        var drive = await DriveService.MyDriveAsync(t.Db, ada.Id);
        using var temp = new TempFolder();
        var store = new BlobStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDirectory"] = temp.Root }).Build());
        var connection = t.Db.Database.GetConnectionString();

        // every upload is a request of its own (its own DbContext), all of them start together
        var start = new TaskCompletionSource();
        var uploads = Enumerable.Range(1, 8).Select(async i =>
        {
            await start.Task;
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
            return await FileService.UploadAsync(db, store, Actor.Of(ada.Id), drive.Id, null, $"Holiday/Day {i % 2}/photo {i}.txt", new MemoryStream(Encoding.UTF8.GetBytes("photo " + i)));
        }).ToList();
        start.SetResult();
        var results = await Task.WhenAll(uploads);

        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        var nodes = await t.Db.Nodes.AsNoTracking().Where(n => n.DriveId == drive.Id && !n.InTrash).ToListAsync();
        var holiday = Assert.Single(nodes, n => n.ParentId == null);
        Assert.Equal("Holiday", holiday.Name);
        var days = nodes.Where(n => n.ParentId == holiday.Id).OrderBy(n => n.Name).ToList();
        Assert.Equal(["Day 0", "Day 1"], days.Select(n => n.Name));
        Assert.All(days, d => Assert.Equal(4, nodes.Count(n => n.ParentId == d.Id && n.Kind == NodeKind.File)));
    }

    [DbFact]
    public async Task Parallel_uploads_of_the_same_new_file_keep_both_under_free_names()
    {
        await using var t = await TestDatabase.CreateAsync();
        var ada = await t.AddUserAsync("Ada");
        var drive = await DriveService.MyDriveAsync(t.Db, ada.Id);
        using var temp = new TempFolder();
        var store = new BlobStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDirectory"] = temp.Root }).Build());
        var connection = t.Db.Database.GetConnectionString();

        var start = new TaskCompletionSource();
        var uploads = Enumerable.Range(1, 4).Select(async i =>
        {
            await start.Task;
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
            return await FileService.UploadAsync(db, store, Actor.Of(ada.Id), drive.Id, null, "report.txt", new MemoryStream(Encoding.UTF8.GetBytes("v" + i)), UploadMode.KeepBoth);
        }).ToList();
        start.SetResult();
        var results = await Task.WhenAll(uploads);

        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        var names = await t.Db.Nodes.AsNoTracking().Where(n => n.DriveId == drive.Id && n.ParentId == null && !n.InTrash).Select(n => n.Name).ToListAsync();
        Assert.Equal(4, names.Count);
        Assert.Equal(4, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("report.txt", names);
    }
}
