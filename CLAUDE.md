# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What MatDrive is

A drive in the browser (like Google Drive) with the functions of the sister app **MatFile** (storage locations of a NAS: local folders, SMB, SFTP, FTP, WebDAV;
zip/unzip, a text editor, content search). Every person has **My Drive**; teams have **shared drives** (members Viewer/Editor/Manager); files and folders are
**shared** with people or through **links** (password, expiry, upload into a folder link); files keep **versions**; deleted items lie in the **trash**; **stars**,
**recent**, **search** (names and the text of text files). **WebDAV** at `/dav/` (sign-in with the address and an app password). **MatOffice** (the sister app, its
own container) opens and saves the office files (`.docx`, `.xlsx`, `.pptx`, `.csv`, the MatOffice formats) with an API key (`Services/Office`). Part of the Mat
family: sign-in through **MatIdentity**, mail through **MatSMTP**, the platform contract for **Mattiverse** (all optional, switched on by environment variables).

Stack: ASP.NET Core on **.NET 10**, C#, **Razor Pages**, EF Core + **PostgreSQL** (metadata), the bytes of the files in the data volume (`/data/files`, `BlobStore`),
the **MatUi** toolkit of the sister project MatDesign (`wwwroot/matui`, release 0.9.3, copied: never edit by hand), plain JavaScript ES modules without a build
step. Storage providers: SMBLibrary, SSH.NET, FluentFTP (like MatFile). Docker-first. Web port **4810** (pgAdmin 4811, local Postgres 4812). UI languages:
English and German (catalog in `Resources/Texts/*.json`). Accent: violet `#8e24aa` (white text 7:1).

## Build / run / test

- **Build:** `dotnet build MatDrive.slnx` (SDK 10, `global.json`). Expected: 0 warnings, 0 errors.
- **Tests:** `dotnet test`. Database tests run when `MATDRIVE_TEST_DB` is set (an administrator connection string, e.g.
  `Host=localhost;Port=4812;Username=matdrive;Password=matdrive;Database=postgres` while the local stack runs); each test creates, migrates and drops a database of its own
  (`WebDavServerTests`: the whole app in memory with `WebApplicationFactory`, one database for the class, every test with its own people).
  `TextCatalogTests` checks every text key of the server code and of the scripts (`t('Drive.X')` → `Js.Drive.X`) in English and German.
- **Run / test the app: always rebuild the container** (`./scripts/redeploy.ps1`, `-Clean` for empty volumes). CSS, JS and cshtml are baked into the image.
- **Test account (always):** `admin@example.com` / `Start12345` – the first account of a fresh stack (it becomes the administrator).
- **Real tests are end to end in a browser** (Playwright with the installed Edge, `channel: 'msedge'`): upload (also drag & drop and a folder), preview, move, share, link,
  trash, versions, a phone (390 px), dark mode; WebDAV with a client (curl / the Windows Explorer); MatOffice with the local MatOffice stack (port 4820).
- **Migrations:** `dotnet ef migrations add <Name> -p src/MatDrive -o Data/Migrations` (local tool in `dotnet-tools.json`; a design-time factory exists). Applied at start-up.
- **Updating MatUi:** take the release of MatDesign (`origin/main`): the CSS is the parts of `src/MatUi/wwwroot/css` joined in the order of the first line of `matui.css`;
  `matui.js`, `matui.boot.js`, `matui-icons.svg` as they are.
- Write files with the editor tools, not with shell heredocs that contain apostrophes or backslashes.

## Map

```
src/MatDrive
  Program.cs                 composition root: services, cookie auth (+ MatIdentity), antiforgery header, razor conventions, rate limit, static files, endpoints
  Infrastructure.cs          HealthCheck (--healthcheck), Database.MigrateWithRetry, SecurityHeaders (CSP), MaintenanceService (InstanceService.CleanUpAsync every 6 h), EmptyStringBinderProvider
  Data/                      Entities.cs (User, UserSession, AppPassword, Drive, DriveMember, Node (Path = "/12/40/41/"), NodeVersion, NodeShare, ShareLink, NodeStar, NodeVisit,
                             Activity, StorageLocation(+Access), UploadSession, SystemSetting), AppDbContext.cs, Migrations/
  Services/Files/            THE business layer of the drives, static classes (permission check in every function):
                             DriveAccess (Actor = person and/or link -> AccessRole: drive role, shares on the node or a folder above, link; null = "not found";
                             administrators have NO access to the files of others), DriveService (My Drive, shared drives, members, usage/quota),
                             NodeService (lists: folder/recent/starred/shared/trash/search; create folder, rename, move, copy, trash, restore, delete forever, stars, visits;
                             names unique per folder, "name (2).ext"), FileService (upload whole / in pieces, new versions with If-Match and coalescing, open for download,
                             versions, zip, unzip, text for the editor), ShareService (shares, links), ActivityLog, FileTypes, BlobStore (/data/files/ab/cd/<blob>),
                             FileEndpoints (/files/{id}/content, /files/zip, /live/drive/*, the file API for MatOffice), LinkEndpoints (/l/{token}/…, LinkGate = the password cookie)
  Services/Office/           OfficeService: settings (URL, internal URL, API key encrypted, MatDrive URL for MatOffice), check (/api/v1/host/formats), open (session at
                             MatOffice, a time-limited token for /api/v1/office/files/{id}/content), new documents (/api/v1/host/new)
  Services/Locations/        storage locations like MatFile (LocationAccess; providers and browsing)
  Services/WebDav/           the WebDAV server at /dav/ (RFC 4918, class 1 and 2): WebDavEndpoints (AddWebDav, MapWebDav), WebDavHandler (.Read: GET/HEAD, PROPFIND,
                             PROPPATCH; .Write: PUT, DELETE, MKCOL, COPY/MOVE, LOCK/UNLOCK), DavStore (the tree over the business layer), DavAuthenticator (Basic),
                             DavLockManager (locks in memory), DavPath (paths, hrefs, Destination), DavIfHeader, DavXml (bodies, DavProperties, DavMultiStatus)
  Services/Accounts/         Profiles, UserAdmin, AppPasswords (WebDAV passwords: shown once, hashed)
  Services/Mail/MailService  MatSMTP through MailKit, queued in the background; off unless MATSMTP_HOST is set
  Services/Auth/             CurrentUser, PasswordService, SessionService (sessions in the DB), AccountService, ExternalSignInService (MatIdentity), AccountDataService (GDPR export/erase)
  Services/                  Ui (icons, logo, bytes), MenuService, InstanceService (numbers, settings, housekeeping), ListModels (toolbar, pager), Dates, Assets
  MatPlatform/               copy of the shared package (MatDesign): platform contract, MatIdentity sign-in, back-channel logout, MatSmtpSettings. Never edit here.
  Platform/                  MatDrivePlatform (tiles, settings, actions for Mattiverse), MatDriveIdentityUsers
  Controls/                  ONE code base per control: <md-field>, <md-buttons>, <md-table>, <md-tabs>/<md-tab>
  Pages/                     Drive/ (Index = the explorer: /drive, /drive/folder/{id}, /drive/shared-drive/{id}, shared, recent, starred, trash, search; Item = the tabs of an item;
                             Edit = the text editor), Drives/ (shared drives), Public/ (links /l/{token}), Account/ (+ AppPasswords), Admin/ (Users, Locations, Integrations,
                             Activity, System), About
  wwwroot/js/                util.js (t, el, icon, request, dialog …), drive.js (the menu "New" on every page), explorer.js (uploads, drag & drop, menus, preview, picker)
  wwwroot/css/app.css        accent + the styles of the explorer (prefix md-)
tests/MatDrive.Tests         unit + database tests
```

## Conventions

**Database (PostgreSQL)**: tables PascalCase and singular, key always `Id` BIGINT, every record has `CreateDate/CreateUserId/UpdateDate/UpdateUserId` (filled by
`AppDbContext.SaveChanges`), `UpdateState` (0 deleted, 1 created, 2 updated) for soft delete (`User`, `Drive`, `Node`, `StorageLocation`); the trash of the drive is
`Node.TrashedDate`/`InTrash` (not UpdateState). A `Token` (uuid) for a security key (`UserSession.Token`, `ShareLink.Token`, `UploadSession.Token`).

**Security**: every query of a node goes through `DriveAccess`; something a person may not see answers 404. Every change of `/live` needs the antiforgery header
`RequestVerificationToken`. File content is served with its own CSP (sandbox) and inline only for types that cannot run script (never HTML or SVG). Secrets
(passwords of locations, the key of MatOffice) are encrypted with DataProtection (keys in the volume); app passwords and API keys only as hashes.

**WebDAV** (`/dav/`): sign-in with HTTP Basic, the address and an app password (or the password of a local account), never the cookie; a sign-in is remembered for 2 minutes,
10 failures a minute per IP answer 429. The tree has stable English names: `My Drive/`, `Shared drives/<drive>/`, `Shared with me/<item>/` (clashing names get ` (2)`), no
trash. Everything goes through the business layer (`DriveAccess` decides: a viewer reads, a write answers 403); its activity is marked `webdav` (`ActivityLog.From`), DELETE
and `Overwrite: T` go into the trash, saves of one person within 2 minutes are one version. Locks live in memory (`DavLockManager`: at most an hour, 500 a person, gone with a
restart; a LOCK on a free name makes an empty file). The quota answers 507 (before the bytes when the length is announced), the upload limit 413. PROPFIND: depth 0 or 1, one
query for the children; PROPPATCH is answered, not stored. The Windows Explorer sends Basic only over HTTPS (or with `BasicAuthLevel=2`); `git push` over http is a client for tests.

**UI (MatDesign rules)**: CRUD = list page (toolbar above, table/gallery, pager under it, actions left below) + separate edit page (Save, Back, space, Delete);
an item and a shared drive have **tabs**. The explorer is the special UI of the app (like a file manager), built on the toolkit. Icons via `Ui.Icon` (toolkit sprite + `wwwroot/img/app-icons.svg`).
Every `td` has `data-label`. The version at the foot of the menu links to `/About`.

**Code**: Microsoft C# guidelines, file-scoped namespaces, `.editorconfig`. The business layer is static classes taking the `AppDbContext` and the actor. No database queries in page
models or views beyond calling the business layer. English identifiers and comments, UI texts only in the catalog.

**Compose files**: no environment variables to set, no placeholders; a stack (`name:`) per file; `docker-compose.yml` (release), `.dev.yml` (nightly + pgAdmin), `.local.yml`
(build + pgAdmin, subnet 10.253.48.0/24).

**Git**: commit under the user's name; never add a Claude/Anthropic co-author trailer or a "generated with" line (global rule of the user; it overrides any default). Branches:
`main` = release, `dev` = development. Versions: release `<major>.<minor>.<build>-<date>`, development `nightly-<build>-<date>`, local `local-<date>` (`Directory.Build.props`;
`.github/workflows/build.yml` builds, tests and pushes the image).
