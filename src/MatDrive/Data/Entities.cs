namespace MatDrive.Data;

/// <summary>What every record has: an Id (BIGINT) and who created and changed it, and when (the columns are filled by <see cref="AppDbContext"/>).</summary>
public abstract class Entity
{
    public long Id { get; set; }

    public DateTime CreateDate { get; set; }

    public long? CreateUserId { get; set; }

    public DateTime UpdateDate { get; set; }

    public long? UpdateUserId { get; set; }
}

/// <summary>A record that supports soft delete: 0 = deleted, 1 = created, 2 = updated. A deleted record is hidden by a query filter.</summary>
public abstract class SoftEntity : Entity
{
    public const short Deleted = 0;
    public const short Created = 1;
    public const short Updated = 2;

    public short UpdateState { get; set; } = Created;
}

public enum UserRole
{
    Admin = 1,
    User = 2,
}

public enum DriveKind
{
    /// <summary>"My Drive" of one person.</summary>
    Personal = 1,

    /// <summary>A shared drive of a team: it belongs to its members, not to a person; files stay when somebody leaves.</summary>
    Shared = 2,
}

/// <summary>What a person may do with a file, a folder or a whole drive (the best of drive membership, sharing and link wins).</summary>
public enum AccessRole
{
    None = 0,

    /// <summary>May see, preview and download.</summary>
    Viewer = 1,

    /// <summary>May also upload, change, rename, move and delete (into the trash).</summary>
    Editor = 2,

    /// <summary>May also share and manage the members of a shared drive.</summary>
    Manager = 3,

    /// <summary>The owner (of a personal drive, or of an item they created in it).</summary>
    Owner = 4,
}

public enum NodeKind
{
    Folder = 1,
    File = 2,
}

public sealed class User : SoftEntity
{
    /// <summary>The address to sign in with; unique, stored in lower case.</summary>
    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.User;

    public bool IsActive { get; set; } = true;

    /// <summary>The IANA time zone dates are shown in.</summary>
    public string TimeZone { get; set; } = "Europe/Berlin";

    /// <summary>The language of the interface: en or de; empty = the language of the browser.</summary>
    public string Language { get; set; } = string.Empty;

    public int FailedLogins { get; set; }

    public DateTime? LockoutUntil { get; set; }

    public DateTime? LastLoginDate { get; set; }

    /// <summary>system, light or dark.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>standard, paper, contrast or oled.</summary>
    public string Scheme { get; set; } = "standard";

    /// <summary>list or grid: how folders are shown.</summary>
    public string View { get; set; } = "list";

    /// <summary>The space of "My Drive" in bytes; null = the default of the installation.</summary>
    public long? QuotaBytes { get; set; }

    /// <summary>Mail about files and folders that are shared with the person (only when the installation can send mail).</summary>
    public bool MailOnShare { get; set; } = true;

    /// <summary>The person of MatIdentity (the claim <c>sub</c>) this account is linked with; null = a local account only.</summary>
    public string? ExternalSubject { get; set; }
}

/// <summary>A signed-in browser. The cookie carries the <see cref="Token"/>; the session lives in the database.</summary>
public sealed class UserSession : Entity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long UserId { get; set; }

    public User? User { get; set; }

    public DateTime LastSeenDate { get; set; }

    public DateTime ExpiresDate { get; set; }

    public bool IsRevoked { get; set; }

    public string? UserAgent { get; set; }

    public string? IpAddress { get; set; }

    /// <summary>The ID token of a sign-in through MatIdentity (for the sign-out there); null for a local sign-in. A secret: never exported.</summary>
    public string? IdentityToken { get; set; }

    /// <summary>The session of MatIdentity (the claim <c>sid</c>) this session started with: the back-channel logout ends it by this.</summary>
    public string? IdentitySessionId { get; set; }
}

/// <summary>
/// A password for one device or program (WebDAV in the Explorer, Finder, a sync tool): it signs in only to WebDAV, never to the pages. Only a hash is kept; a person
/// who signs in through MatIdentity has no password of their own, so this is their way to WebDAV.
/// </summary>
public sealed class AppPassword : Entity
{
    public long UserId { get; set; }

    public User? User { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 of the password (hex).</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>The first characters, to recognise it in the list.</summary>
    public string Prefix { get; set; } = string.Empty;

    public DateTime? LastUsedDate { get; set; }
}

/// <summary>A drive: the "My Drive" of a person, or a shared drive of a team. Files and folders belong to exactly one drive.</summary>
public sealed class Drive : SoftEntity
{
    public DriveKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The person of a personal drive; null for a shared drive.</summary>
    public long? OwnerId { get; set; }

    public User? Owner { get; set; }

    /// <summary>The space of a shared drive in bytes (null = no limit); a personal drive takes the quota of its person.</summary>
    public long? QuotaBytes { get; set; }

    /// <summary>The bytes of every version of every file, the trash included (kept up to date by the business layer).</summary>
    public long UsedBytes { get; set; }

    /// <summary>A colour of a shared drive (#rrggbb).</summary>
    public string? Colour { get; set; }

    public string? Description { get; set; }

    public ICollection<DriveMember> Members { get; set; } = [];
}

/// <summary>A person in a shared drive with their role (Viewer, Editor, Manager).</summary>
public sealed class DriveMember : Entity
{
    public long DriveId { get; set; }

    public Drive? Drive { get; set; }

    public long UserId { get; set; }

    public User? User { get; set; }

    public AccessRole Role { get; set; } = AccessRole.Editor;
}

/// <summary>
/// A file or a folder. <see cref="Path"/> is the chain of ids from the top of the drive to the node itself (<c>/12/40/41/</c>), so the parents and everything below a
/// folder are found with one query. A node in the trash keeps its place: <see cref="TrashedDate"/> marks the item that was deleted (and everything below it is
/// hidden with it); restoring puts it back where it was.
/// </summary>
public sealed class Node : SoftEntity
{
    public long DriveId { get; set; }

    public Drive? Drive { get; set; }

    /// <summary>The folder it lies in; null = the top of the drive.</summary>
    public long? ParentId { get; set; }

    public Node? Parent { get; set; }

    public NodeKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The ids from the top of the drive to this node, each followed by a slash: <c>/12/40/41/</c>.</summary>
    public string Path { get; set; } = "/";

    /// <summary>The bytes of the current version (a folder: 0).</summary>
    public long Size { get; set; }

    public string? MimeType { get; set; }

    /// <summary>The current version of a file.</summary>
    public long? CurrentVersionId { get; set; }

    /// <summary>The person who created the node (in a personal drive: always its owner).</summary>
    public long OwnerId { get; set; }

    /// <summary>When the content of a file changed last (a folder: when something in it changed).</summary>
    public DateTime ModifiedDate { get; set; }

    public long? ModifiedById { get; set; }

    /// <summary>When the node was put into the trash (itself, not because a folder above it was); null while it is not.</summary>
    public DateTime? TrashedDate { get; set; }

    public long? TrashedById { get; set; }

    /// <summary>True for everything that is in the trash, also through a folder above (the lists hide these).</summary>
    public bool InTrash { get; set; }

    public string? Description { get; set; }

    /// <summary>The colour of a folder (#rrggbb), chosen by a person.</summary>
    public string? Colour { get; set; }

    /// <summary>The first characters of a text file (the search looks into them); null for other files.</summary>
    public string? TextContent { get; set; }

    public ICollection<NodeShare> Shares { get; set; } = [];
}

/// <summary>One state of a file. The bytes lie in the data volume under <see cref="BlobId"/> (<c>/data/files/ab/cd/&lt;blob&gt;</c>).</summary>
public sealed class NodeVersion : Entity
{
    public long NodeId { get; set; }

    public Node? Node { get; set; }

    /// <summary>1, 2, 3 … per file.</summary>
    public int Number { get; set; }

    public Guid BlobId { get; set; } = Guid.NewGuid();

    public long Size { get; set; }

    /// <summary>SHA-256 of the bytes (hex): the ETag of the file, and a check after a copy.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public string? MimeType { get; set; }

    /// <summary>Keep this version for good (otherwise old versions are thinned out after the days of the system settings).</summary>
    public bool KeepForever { get; set; }

    /// <summary>Where the version came from: upload, webdav, office, restore, editor, copy.</summary>
    public string Source { get; set; } = "upload";

    public string? Comment { get; set; }
}

/// <summary>A file or folder shared with a person (the access holds for everything below a folder).</summary>
public sealed class NodeShare : Entity
{
    public long NodeId { get; set; }

    public Node? Node { get; set; }

    public long UserId { get; set; }

    public User? User { get; set; }

    public AccessRole Role { get; set; } = AccessRole.Viewer;
}

/// <summary>A link to a file or folder that anybody who has it can open (optionally with a password, until a date).</summary>
public sealed class ShareLink : Entity
{
    public long NodeId { get; set; }

    public Node? Node { get; set; }

    /// <summary>The secret of the address <c>/l/&lt;token&gt;</c>.</summary>
    public Guid Token { get; set; } = Guid.NewGuid();

    /// <summary>Viewer: look and download; Editor: a folder link also takes uploads.</summary>
    public AccessRole Role { get; set; } = AccessRole.Viewer;

    /// <summary>A hash of the password of the link (PBKDF2, like the passwords of accounts); null = none.</summary>
    public string? PasswordHash { get; set; }

    public DateTime? ExpiresDate { get; set; }

    /// <summary>False: the link shows and previews, but offers no download.</summary>
    public bool AllowDownload { get; set; } = true;

    public int Visits { get; set; }

    public int Downloads { get; set; }

    public string? Note { get; set; }
}

/// <summary>A file or folder a person starred.</summary>
public sealed class NodeStar : Entity
{
    public long NodeId { get; set; }

    public Node? Node { get; set; }

    public long UserId { get; set; }
}

/// <summary>When a person opened a file last (the list "Recent").</summary>
public sealed class NodeVisit : Entity
{
    public long NodeId { get; set; }

    public Node? Node { get; set; }

    public long UserId { get; set; }

    public DateTime VisitDate { get; set; }
}

/// <summary>What happened in the drives: uploaded, renamed, moved, shared … (the activity of a file and the log of the administration).</summary>
public sealed class Activity : Entity
{
    public long? UserId { get; set; }

    public long? DriveId { get; set; }

    public long? NodeId { get; set; }

    /// <summary>upload, version, create, rename, move, copy, trash, restore, delete, share, unshare, link, download, webdav, office, location …</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>The name of the item at the time (it may be gone later).</summary>
    public string? Target { get; set; }

    public string? Detail { get; set; }

    public DateTime OccurredDate { get; set; }
}

public enum LocationKind
{
    /// <summary>A folder of the server (a bind mount of the NAS below /storage).</summary>
    Local = 1,
    Smb = 2,
    Sftp = 3,
    Ftp = 4,
    WebDav = 5,
}

/// <summary>
/// A storage location outside the drives (like MatFile): a folder of the server or a share of another machine, browsed live. Its files are not in the database;
/// the administrators decide who may see and change it.
/// </summary>
public sealed class StorageLocation : SoftEntity
{
    public string Name { get; set; } = string.Empty;

    public LocationKind Kind { get; set; }

    /// <summary>The folder (local: inside the container, e.g. /storage/photos; remote: the path on the server).</summary>
    public string RootPath { get; set; } = "/";

    public string? Host { get; set; }

    public int? Port { get; set; }

    /// <summary>The share of an SMB server (empty: every share is a folder on the top level).</summary>
    public string? Share { get; set; }

    public string? Username { get; set; }

    /// <summary>The password, encrypted with the keys of the data volume (DataProtection). Never sent to a browser. With a private key (SFTP) it is the passphrase of the key.</summary>
    public string? SecretProtected { get; set; }

    /// <summary>The domain of an SMB account (empty: the account of the server itself).</summary>
    public string? Domain { get; set; }

    /// <summary>The address of a WebDAV share (https://cloud.example.com/remote.php/dav/files/anna).</summary>
    public string? Url { get; set; }

    /// <summary>FTP: an encrypted connection (FTPS, explicit TLS).</summary>
    public bool UseTls { get; set; }

    /// <summary>FTPS and WebDAV over https: accept a certificate that is not trusted (a NAS with a certificate of its own).</summary>
    public bool AcceptAnyCertificate { get; set; }

    /// <summary>SFTP: the private key (OpenSSH or PEM), encrypted like the password. Never sent to a browser.</summary>
    public string? PrivateKeyProtected { get; set; }

    public bool IsReadOnly { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Every person may see it (with <see cref="EveryoneRole"/>); otherwise only the people in <see cref="Access"/>.</summary>
    public bool ForEveryone { get; set; }

    public AccessRole EveryoneRole { get; set; } = AccessRole.Viewer;

    public string? Note { get; set; }

    public ICollection<StorageLocationAccess> Access { get; set; } = [];
}

public sealed class StorageLocationAccess : Entity
{
    public long LocationId { get; set; }

    public StorageLocation? Location { get; set; }

    public long UserId { get; set; }

    public User? User { get; set; }

    public AccessRole Role { get; set; } = AccessRole.Viewer;
}

/// <summary>An upload in pieces (large files): the pieces are written to a temporary file in the data volume until the last one arrives.</summary>
public sealed class UploadSession : Entity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long UserId { get; set; }

    public long DriveId { get; set; }

    public long? ParentId { get; set; }

    /// <summary>The path of the file below the folder (an upload of a folder: <c>photos/2026/a.jpg</c>).</summary>
    public string RelativePath { get; set; } = string.Empty;

    public long Size { get; set; }

    public long Received { get; set; }

    public DateTime ExpiresDate { get; set; }
}

/// <summary>The few settings of the whole installation (one row, Id 1).</summary>
public sealed class SystemSetting : Entity
{
    /// <summary>Whether anyone may create an account (the first account of an installation can always sign up and becomes the administrator).</summary>
    public bool RegistrationOpen { get; set; }

    /// <summary>The address the app is reached at from outside (links in mails and of shared links); empty = the address of the request.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>The space of "My Drive" of a person that has no quota of their own (bytes; 0 = no limit).</summary>
    public long DefaultQuotaBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>The largest file that may be uploaded (bytes; 0 = no limit).</summary>
    public long MaxUploadBytes { get; set; }

    /// <summary>How long deleted items stay in the trash.</summary>
    public int TrashDays { get; set; } = 30;

    /// <summary>Older versions of a file (not marked "keep forever") are removed after these days; 0 = keep all.</summary>
    public int VersionDays { get; set; } = 90;

    /// <summary>MatOffice: on/off and where it is.</summary>
    public bool OfficeEnabled { get; set; }

    /// <summary>The address of MatOffice for the browser (https://office.example.com).</summary>
    public string? OfficeUrl { get; set; }

    /// <summary>The address under which MatDrive reaches MatOffice (inside Docker, e.g. http://matoffice-web:4820); empty = <see cref="OfficeUrl"/>.</summary>
    public string? OfficeInternalUrl { get; set; }

    /// <summary>The API key MatOffice made for MatDrive, encrypted (DataProtection).</summary>
    public string? OfficeKeyProtected { get; set; }

    /// <summary>The address under which MatOffice reaches MatDrive to read and write files; empty = the public address.</summary>
    public string? DriveUrlForOffice { get; set; }

    /// <summary>What MatOffice answered on the last check (JSON of /api/v1/host/formats): which files it opens and writes.</summary>
    public string? OfficeFormats { get; set; }

    public DateTime? OfficeCheckedDate { get; set; }
}
