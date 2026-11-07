using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Files;
using Microsoft.AspNetCore.Http.Features;

namespace MatDrive.Services.WebDav;

/// <summary>Writing: PUT (a new file or a new version), DELETE (into the trash), MKCOL, COPY and MOVE, LOCK and UNLOCK.</summary>
public sealed partial class WebDavHandler
{
    /// <summary>
    /// Saves of the same person within this time become one version (an editor that saves twice in a row; the empty file the Windows Explorer makes before it
    /// writes the content).
    /// </summary>
    public static readonly TimeSpan Coalesce = TimeSpan.FromMinutes(2);

    /// <summary>An empty version (a placeholder of a client) is replaced by the next save of the same person for this long.</summary>
    public static readonly TimeSpan CoalesceEmpty = TimeSpan.FromHours(12);

    /// <summary>
    /// PUT: the bytes are streamed into the store, never held in memory. A file of that name gets a new version (<c>If-Match</c>, <c>If-None-Match: *</c> and the
    /// lock tokens of the If header are checked before the first byte), otherwise a new file is made (201). 413 above the limit of the installation, 507 when the
    /// drive is full.
    /// </summary>
    private async Task PutAsync()
    {
        if (Request.Headers.ContentRange.Count > 0)
        {
            // a partial PUT is not supported (RFC 9110, 9.3.4)
            Status(StatusCodes.Status400BadRequest);
            return;
        }

        var lookup = await ResolveAsync(_segments);
        if (lookup.Target is { } target)
        {
            if (target.IsCollection)
            {
                Response.Headers.Allow = Methods;
                Status(StatusCodes.Status405MethodNotAllowed);
                return;
            }

            if (!target.CanEdit)
            {
                Status(StatusCodes.Status403Forbidden);
                return;
            }

            var ifMatch = Request.Headers.IfMatch.ToString().Trim();
            var matches = ifMatch.Length == 0 || ifMatch == "*" || ifMatch.Split(',').Any(t => target.Item.Sha256 is { } sha && DavIfHeader.SameTag(t, sha));
            if (!matches || Request.Headers.IfNoneMatch.ToString().Trim() == "*")
            {
                Status(StatusCodes.Status412PreconditionFailed);
                return;
            }

            if (!await PreconditionsAsync(StateOf(target)) || !await UnlockedAsync(WriteLocks(target)) || !await RoomForAsync(target.Drive!.Id, target.Node!.Size))
            {
                return;
            }

            AllowLargeBody();
            var single = ifMatch.Length > 0 && ifMatch != "*" && !ifMatch.Contains(',') ? ifMatch : null;
            var (status, _, version) = await FileService.WriteAsync(_db, _store, Actor, target.Node.Id, Request.Body, Source, single,
                target.Node.Size == 0 ? CoalesceEmpty : Coalesce, _ct);
            Written(status, version, created: false);
            return;
        }

        if (lookup.Parent is not { } parent)
        {
            Status(StatusCodes.Status409Conflict);
            return;
        }

        if (!await CanAddAsync(parent, lookup.Name!) || !await PreconditionsAsync(StateOfNew(parent)) || !await UnlockedAsync(AddLocks(parent)))
        {
            return;
        }

        if (Request.Headers.IfMatch.Count > 0)
        {
            // there is nothing to match
            Status(StatusCodes.Status412PreconditionFailed);
            return;
        }

        if (!await RoomForAsync(parent.Drive!.Id, 0))
        {
            return;
        }

        AllowLargeBody();
        var (made, _, first) = await FileService.CreateAsync(_db, _store, Actor, parent.Drive.Id, parent.FolderId, DavPath.Nfc(lookup.Name!), Request.Body, Source, _ct);
        Written(made, first, created: true);
    }

    private void Written(WriteStatus status, NodeVersion? version, bool created)
    {
        switch (status)
        {
            case WriteStatus.Written or WriteStatus.Created when version is not null:
                Response.Headers.ETag = Quote(version.Sha256);
                Status(created ? StatusCodes.Status201Created : StatusCodes.Status204NoContent);
                break;
            case WriteStatus.Conflict:
                Status(created ? StatusCodes.Status409Conflict : StatusCodes.Status412PreconditionFailed);
                break;
            case WriteStatus.TooLarge:
                Status(StatusCodes.Status413PayloadTooLarge);
                break;
            case WriteStatus.QuotaFull:
                Status(StatusCodes.Status507InsufficientStorage);
                break;
            default:
                Status(StatusCodes.Status403Forbidden);
                break;
        }
    }

    /// <summary>Whether something new of this name may go into a place: a folder or a drive, the role Editor, an allowed name. Answers the status when not.</summary>
    private Task<bool> CanAddAsync(DavTarget parent, string name)
    {
        var status = !parent.IsContainer ? (parent.Place == DavPlace.Node ? StatusCodes.Status409Conflict : StatusCodes.Status403Forbidden)
            : !parent.CanAdd ? StatusCodes.Status403Forbidden
            : !IsAllowedName(name) ? StatusCodes.Status400BadRequest
            : 0;
        if (status != 0)
        {
            Status(status);
        }

        return Task.FromResult(status == 0);
    }

    /// <summary>A name MatDrive keeps as it is (it would clean the others, and the client would not find its file again).</summary>
    private static bool IsAllowedName(string name)
    {
        var nfc = DavPath.Nfc(name);
        return NodeService.CleanName(nfc) == nfc;
    }

    /// <summary>Answers 413 or 507 before the first byte when the announced size is above the limit of the installation or the room of the drive.</summary>
    private async Task<bool> RoomForAsync(long driveId, long replaced)
    {
        var length = Request.ContentLength
                     ?? (long.TryParse(Request.Headers["X-Expected-Entity-Length"].ToString(), out var expected) && expected >= 0 ? expected : null);
        if (length is not { } size)
        {
            return true;
        }

        var max = await FileService.MaxUploadAsync(_db, _ct);
        if (max > 0 && size > max)
        {
            Status(StatusCodes.Status413PayloadTooLarge);
            return false;
        }

        if (!await FileService.HasRoomAsync(_db, driveId, Math.Max(0, size - replaced), _ct))
        {
            Status(StatusCodes.Status507InsufficientStorage);
            return false;
        }

        return true;
    }

    /// <summary>A file may be as large as the installation allows (checked while the bytes arrive), not just the default limit of the server.</summary>
    private void AllowLargeBody()
    {
        if (_http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = null;
        }
    }

    /// <summary>DELETE: the file or folder goes into the trash of its drive (with everything below it), like in the explorer.</summary>
    private async Task DeleteAsync()
    {
        var lookup = await ResolveAsync(_segments);
        if (lookup.Target is not { } target)
        {
            Status(StatusCodes.Status404NotFound);
            return;
        }

        if (!target.CanEdit)
        {
            Status(StatusCodes.Status403Forbidden);
            return;
        }

        var depth = Request.Headers["Depth"].ToString().Trim();
        if (target.IsCollection && depth.Length > 0 && !depth.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            Status(StatusCodes.Status400BadRequest);
            return;
        }

        if (!await PreconditionsAsync(StateOf(target)) || !await UnlockedAsync(RemoveLocks(target)))
        {
            return;
        }

        if (await NodeService.TrashAsync(_db, Actor, [target.Node!.Id], _ct) == 0)
        {
            Status(StatusCodes.Status403Forbidden);
            return;
        }

        _locks.RemoveSubtree(target.Chain);
        Status(StatusCodes.Status204NoContent);
    }

    /// <summary>MKCOL: a new folder (405 when the name is taken, 409 when the folder above is missing, 415 with a body).</summary>
    private async Task MkColAsync()
    {
        if (await HasBodyAsync())
        {
            Status(StatusCodes.Status415UnsupportedMediaType);
            return;
        }

        var lookup = await ResolveAsync(_segments);
        if (lookup.Target is not null)
        {
            Response.Headers.Allow = Methods;
            Status(StatusCodes.Status405MethodNotAllowed);
            return;
        }

        if (lookup.Parent is not { } parent)
        {
            Status(StatusCodes.Status409Conflict);
            return;
        }

        if (!await CanAddAsync(parent, lookup.Name!) || !await PreconditionsAsync(StateOfNew(parent)) || !await UnlockedAsync(AddLocks(parent)))
        {
            return;
        }

        var made = await NodeService.CreateFolderAsync(_db, Actor, parent.Drive!.Id, parent.FolderId, DavPath.Nfc(lookup.Name!), _ct);
        Status(made.Ok ? StatusCodes.Status201Created : made.NotAllowed ? StatusCodes.Status403Forbidden : StatusCodes.Status405MethodNotAllowed);
    }

    /// <summary>
    /// COPY and MOVE: the <c>Destination</c> is an absolute URL or path below <c>/dav</c>; <c>Overwrite: F</c> refuses an existing destination (412), otherwise
    /// it goes into the trash first (204). A MOVE within the same folder is a rename; between folders and drives it is a move of the business layer, then a rename
    /// to the name of the destination. 201 when the destination is new.
    /// </summary>
    private async Task CopyMoveAsync(bool move)
    {
        var destination = DavPath.ParseUrl(Request.Headers["Destination"].ToString(), PathBase, out var foreign);
        if (destination is null)
        {
            Status(foreign ? StatusCodes.Status502BadGateway : StatusCodes.Status400BadRequest);
            return;
        }

        var overwriteHeader = Request.Headers["Overwrite"].ToString().Trim();
        if (overwriteHeader is not ("" or "T" or "t" or "F" or "f"))
        {
            Status(StatusCodes.Status400BadRequest);
            return;
        }

        var overwrite = !overwriteHeader.Equals("F", StringComparison.OrdinalIgnoreCase);
        var depth = Request.Headers["Depth"].ToString().Trim();
        var shallow = depth == "0";
        if (depth.Length > 0 && !shallow && !depth.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            Status(StatusCodes.Status400BadRequest);
            return;
        }

        var lookup = await ResolveAsync(_segments);
        if (lookup.Target is not { } source)
        {
            Status(StatusCodes.Status404NotFound);
            return;
        }

        if (source.Place != DavPlace.Node || destination.Count == 0 || (move && shallow && source.IsCollection))
        {
            // the places above the files stay where they are; a folder moves with everything in it
            Status(move && shallow && source.IsCollection ? StatusCodes.Status400BadRequest : StatusCodes.Status403Forbidden);
            return;
        }

        if (move && !source.CanEdit)
        {
            Status(StatusCodes.Status403Forbidden);
            return;
        }

        // the same path: nothing to do, unless a MOVE only changes the case of the name (a rename)
        var name = DavPath.Nfc(destination[^1]);
        var samePath = DavPath.Key(destination) == DavPath.Key(_segments);
        var caseRename = samePath && move && name != DavPath.Nfc(_segments[^1]);
        if (samePath && !caseRename)
        {
            Status(StatusCodes.Status403Forbidden);
            return;
        }

        var destinationLookup = caseRename ? DavLookup.Missing : await ResolveAsync(destination);
        var existing = destinationLookup.Target;
        if (existing?.Node?.Id == source.Node!.Id)
        {
            // the same item reached on another path
            Status(StatusCodes.Status403Forbidden);
            return;
        }

        var parent = caseRename ? null : existing is null ? destinationLookup.Parent : (await ResolveAsync(destination.Take(destination.Count - 1).ToList())).Target;
        if (!caseRename && parent is null)
        {
            Status(StatusCodes.Status409Conflict);
            return;
        }

        // a shared item renamed at the top of "Shared with me" keeps its place and gets the new name
        var renameShared = move && parent?.Place == DavPlace.SharedWithMe && source.ViaShare && _segments.Count == 2;
        var renameOnly = caseRename || renameShared;
        if (!renameOnly && !await CanAddAsync(parent!, name))
        {
            return;
        }

        if (renameOnly && !IsAllowedName(name))
        {
            Status(StatusCodes.Status400BadRequest);
            return;
        }

        if (!renameOnly && source.IsCollection && parent!.Node is { } into && DriveAccess.IdsOf(into.Path).Contains(source.Node.Id))
        {
            // a folder never goes into itself
            Status(StatusCodes.Status409Conflict);
            return;
        }

        if (existing is not null)
        {
            if (!overwrite)
            {
                Status(StatusCodes.Status412PreconditionFailed);
                return;
            }

            if (!existing.CanEdit)
            {
                Status(StatusCodes.Status403Forbidden);
                return;
            }
        }

        var destinationState = existing is not null ? StateOf(existing) : parent is not null ? StateOfNew(parent) : DavIfState.None;
        if (!await PreconditionsAsync(StateOf(source), destination, destinationState))
        {
            return;
        }

        var groups = new List<IReadOnlyCollection<DavLock>>();
        if (move)
        {
            groups.AddRange(RemoveLocks(source));
        }

        if (existing is not null)
        {
            groups.AddRange(RemoveLocks(existing));
        }
        else if (!renameOnly)
        {
            groups.AddRange(AddLocks(parent!));
        }

        if (!await UnlockedAsync(groups))
        {
            return;
        }

        if (existing is not null)
        {
            // Overwrite: T – what was there goes into the trash (it can be restored from there)
            if (await NodeService.TrashAsync(_db, Actor, [existing.Node!.Id], _ct) == 0)
            {
                Status(StatusCodes.Status403Forbidden);
                return;
            }

            _locks.RemoveSubtree(existing.Chain);
        }

        var done = move ? await MoveAsync(source, parent, name, renameOnly) : await CopyAsync(source, parent!, name, shallow);
        if (!done)
        {
            return;
        }

        if (move)
        {
            // a lock does not follow its resource
            _locks.RemoveSubtree(source.Chain);
        }

        Status(existing is not null ? StatusCodes.Status204NoContent : StatusCodes.Status201Created);
    }

    private async Task<bool> MoveAsync(DavTarget source, DavTarget? parent, string name, bool renameOnly)
    {
        var node = source.Node!;
        if (!renameOnly && (parent!.Drive!.Id != node.DriveId || parent.FolderId != node.ParentId))
        {
            var moved = await NodeService.MoveAsync(_db, Actor, [node.Id], parent.Drive.Id, parent.FolderId, _ct);
            if (!moved.Ok || moved.Value == 0)
            {
                Status(moved.Ok || moved.NotAllowed ? StatusCodes.Status403Forbidden : StatusCodes.Status409Conflict);
                return false;
            }
        }

        // the name of the destination (a move keeps the old name, numbered when it is taken there)
        var renamed = await NodeService.RenameAsync(_db, Actor, node.Id, name, _ct);
        if (!renamed.Ok)
        {
            Status(renamed.NotAllowed ? StatusCodes.Status403Forbidden : StatusCodes.Status409Conflict);
            return false;
        }

        return true;
    }

    private async Task<bool> CopyAsync(DavTarget source, DavTarget parent, string name, bool shallow)
    {
        if (source.IsCollection && shallow)
        {
            // Depth: 0 copies a folder without what is in it
            var made = await NodeService.CreateFolderAsync(_db, Actor, parent.Drive!.Id, parent.FolderId, name, _ct);
            if (!made.Ok)
            {
                Status(made.NotAllowed ? StatusCodes.Status403Forbidden : StatusCodes.Status409Conflict);
            }

            return made.Ok;
        }

        var copied = await NodeService.CopyToAsync(_db, _store, Actor, source.Node!.Id, parent.Drive!.Id, parent.FolderId, name, _ct);
        if (!copied.Ok)
        {
            Status(copied.NotAllowed ? StatusCodes.Status403Forbidden
                : copied.Error == Text.T("Files.QuotaFull") ? StatusCodes.Status507InsufficientStorage
                : StatusCodes.Status409Conflict);
        }

        return copied.Ok;
    }

    /// <summary>
    /// LOCK: a new exclusive or shared write lock (Depth 0 or infinity) with the timeout the client asks for (within bounds), or – without a body – the refresh
    /// of a lock named in the If header. A new name gets an empty file that is locked (RFC 4918, 7.3: 201); the PUT that follows writes into it.
    /// </summary>
    private async Task LockAsync()
    {
        var (doc, error) = await ReadXmlAsync();
        if (error != 0)
        {
            Status(error);
            return;
        }

        var timeout = DavLockManager.ParseTimeout(Request.Headers["Timeout"].ToString());
        var lookup = await ResolveAsync(_segments);
        if (doc is null)
        {
            await RefreshAsync(lookup.Target, timeout);
            return;
        }

        var info = DavXml.LockInfo(doc);
        var depth = Request.Headers["Depth"].ToString().Trim();
        if (info is null || (depth is not ("" or "0") && !depth.Equals("infinity", StringComparison.OrdinalIgnoreCase)))
        {
            Status(StatusCodes.Status400BadRequest);
            return;
        }

        if (lookup.Target is { } target)
        {
            if (!target.CanEdit)
            {
                // the places above the files cannot be locked; a viewer does not lock others out
                Status(StatusCodes.Status403Forbidden);
                return;
            }

            if (await PreconditionsAsync(StateOf(target)))
            {
                await LockNodeAsync(target.Chain, info, target.IsCollection && depth != "0", timeout, Href(_segments, target.IsCollection), created: false);
            }

            return;
        }

        if (lookup.Parent is not { } parent)
        {
            Status(StatusCodes.Status409Conflict);
            return;
        }

        if (!await CanAddAsync(parent, lookup.Name!) || !await PreconditionsAsync(StateOfNew(parent)) || !await UnlockedAsync(AddLocks(parent)))
        {
            return;
        }

        if (_locks.FindConflict([.. parent.Chain, 0], info.Exclusive, false) is { } conflict)
        {
            await ErrorAsync(StatusCodes.Status423Locked, "no-conflicting-lock", conflict.Root);
            return;
        }

        var (status, node, _) = await FileService.CreateAsync(_db, _store, Actor, parent.Drive!.Id, parent.FolderId, DavPath.Nfc(lookup.Name!), Stream.Null, Source, _ct);
        if (status != WriteStatus.Created || node is null)
        {
            Written(status, null, created: true);
            return;
        }

        await LockNodeAsync(DriveAccess.IdsOf(node.Path), info, false, timeout, Href(_segments, false), created: true);
    }

    private async Task RefreshAsync(DavTarget? target, TimeSpan timeout)
    {
        if (target is null)
        {
            Status(StatusCodes.Status404NotFound);
            return;
        }

        var tokens = DavIfHeader.Tokens(_if);
        if (tokens.Count == 0)
        {
            Status(StatusCodes.Status400BadRequest);
            return;
        }

        var chain = target.Chain;
        var refreshed = tokens.Select(t => _locks.Refresh(t, _user.Id, chain, timeout)).FirstOrDefault(l => l is not null);
        if (refreshed is null)
        {
            await ErrorAsync(StatusCodes.Status412PreconditionFailed, "lock-token-matches-request-uri");
            return;
        }

        await XmlAsync(StatusCodes.Status200OK, DavXml.LockAnswer(refreshed, _locks.Now));
    }

    private async Task LockNodeAsync(long[] chain, DavLockInfo info, bool deep, TimeSpan timeout, string root, bool created)
    {
        var (outcome, made, conflict) = _locks.Lock(_user.Id, chain, info.Exclusive, deep, root, info.Owner, timeout);
        switch (outcome)
        {
            case DavLockOutcome.Locked:
                Response.Headers["Lock-Token"] = "<" + made!.Token + ">";
                await XmlAsync(created ? StatusCodes.Status201Created : StatusCodes.Status200OK, DavXml.LockAnswer(made, _locks.Now));
                break;
            case DavLockOutcome.Conflict:
                await ErrorAsync(StatusCodes.Status423Locked, "no-conflicting-lock", conflict!.Root);
                break;
            default:
                Status(StatusCodes.Status503ServiceUnavailable);
                break;
        }
    }

    /// <summary>UNLOCK with the token of the <c>Lock-Token</c> header: only the person who holds the lock ends it (a lock of a resource that is gone ends too).</summary>
    private async Task UnlockAsync()
    {
        var header = Request.Headers["Lock-Token"].ToString().Trim();
        if (header.Length < 3 || header[0] != '<' || header[^1] != '>')
        {
            Status(StatusCodes.Status400BadRequest);
            return;
        }

        var lookup = await ResolveAsync(_segments);
        switch (_locks.Unlock(header[1..^1].Trim(), _user.Id, lookup.Target?.Chain))
        {
            case DavUnlockOutcome.Unlocked:
                Status(StatusCodes.Status204NoContent);
                break;
            case DavUnlockOutcome.Forbidden:
                Status(StatusCodes.Status403Forbidden);
                break;
            default:
                await ErrorAsync(StatusCodes.Status409Conflict, "lock-token-matches-request-uri");
                break;
        }
    }
}
