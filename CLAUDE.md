# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What MatDrive is

A drive in the browser (like Google Drive) with the functions of the sister app **MatFile** (storage locations of a NAS: local folders, SMB, SFTP, FTP, WebDAV;
zip/unzip, a text editor, content search). Every person has **My Drive**; teams have **shared drives** (members Viewer/Editor/Manager); files and folders are
**shared** with people or through **links** (password, expiry, upload into a folder link); files keep **versions**; deleted items lie in the **trash**; **stars**,
**recent**, **search** (names and the text of text files). **WebDAV** at `/dav/` (sign-in with the address and an app password). **MatOffice** (the sister app, its
own container) opens and saves the office files (`.docx`, `.xlsx`, `.pptx`, `.csv`, the MatOffice formats) through the capability `office.edit` (`Services/Office`). Part
of the Mat family on **MatPlatform 2.4** (the Mat app model, MatDesign `docs/APP-MODEL.md`): MatDrive **consumes** `office.edit`, `identity.oidc` (sign-in through
**MatIdentity**) and `mail.smtp` (mail through **MatSMTP**), all optional; a broker (**Mattiverse**) makes the bindings and passes them as environment variables,
without it an administrator enters `office.edit` by hand in *Administration → Connections* (`<mat-connections />`); it **contributes** the launcher Drive and two
admin widgets to a shell. It names one **quota** per person, `storage` (the space of My Drive): MatIdentity may set it (claim `mat_quotas`); the sign-in keeps it on the account
(`User.IdentityQuotaSet` / `IdentityQuotaBytes`), `DriveService.UsageAsync` counts it before the person's own quota and the default, the person's page shows it as fixed, and
`GET /api/quotas/{subject}` answers what is used. No code knows another app beyond the capability, and MatDrive works without any of the variables.

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
  `LocationRemoteTests` run the providers against real SFTP/SMB/FTP/WebDAV servers when `MATDRIVE_TEST_LOCATIONS` is set (`loctest` = the throwaway compose project
  `matdrive-loctest`: SFTP 47222 `/upload`, SMB 47445 share `public`, FTP 47021, WebDAV 47080, `tester`/`Secret123`; or `host=…;sftp=…;smb=…;ftp=…;webdav=…;user=…;password=…`).
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
  Infrastructure.cs          HealthCheck (--healthcheck), Database.MigrateWithRetry (+ MoveOldOfficeSettings: the old Integrations settings become the binding by hand, once),
                             SecurityHeaders (CSP), MaintenanceService (InstanceService.CleanUpAsync every 6 h), EmptyStringBinderProvider
  Data/                      Entities.cs (User, UserSession, AppPassword, Drive, DriveMember, Node (Path = "/12/40/41/"), NodeVersion, NodeShare, ShareLink, NodeStar, NodeVisit,
                             Activity, StorageLocation(+Access), UploadSession, SystemSetting), AppDbContext.cs, Migrations/
  Services/Files/            THE business layer of the drives, static classes (permission check in every function):
                             DriveAccess (Actor = person and/or link -> AccessRole: drive role, shares on the node or a folder above, link; null = "not found";
                             administrators have NO access to the files of others), DriveService (My Drive, shared drives, members, usage/quota),
                             NodeService (lists: folder/recent/starred/shared/trash/search; create folder, rename, move, copy, trash, restore, delete forever, stars, visits;
                             names unique per folder, "name (2).ext"), FileService (upload whole / in pieces, new versions with If-Match and coalescing, open for download,
                             versions, zip, unzip, text for the editor), ShareService (shares, links), ActivityLog, FileTypes, BlobStore (/data/files/ab/cd/<blob>),
                             FileEndpoints (/files/{id}/content, /files/zip, /live/drive/*, the file API for MatOffice), LinkEndpoints (/l/{token}/…, LinkGate = the password cookie)
  Services/Office/           OfficeService: the binding office.edit (MatBindings: Url for the browser, ServerUrl + Key for the calls; bound = on), the formats per binding
                             (Fingerprint, NeedsRefresh: new binding, 6 h, a failure after 5 min; a failure = no kinds + health office.edit degraded), open (session at
                             the editor, a time-limited token for /api/v1/office/files/{id}/content at MAT_INTERNAL_URL ?? the address for other apps ?? the public one),
                             new documents; the endpoints at /api/provides/office.edit/{formats,sessions,new}, else /api/v1/host/* of an older MatOffice;
                             MoveOldSettingsAsync. OfficeMonitor (background: fetches the formats when due, every minute a look)
  Services/Locations/        the storage locations (like MatFile), static business layer: LocationAccess (role of a person: everyone/list, read-only = Viewer),
                             LocationAdmin (administrators: list, save with secrets encrypted, test, delete, access), LocationService (+ .Transfers: browse, upload, folder,
                             rename, delete, text, properties/rwx, move/copy across locations, to and from the drives, zip, unzip), LocationSearch (below a folder, NDJSON);
                             ILocationProvider + LocalProvider/SmbProvider/SftpProvider/FtpProvider/WebDavProvider, PathGuard (every path), ConnectionPool (lent
                             connections), LocationConnections (singleton: providers, secrets, reachability in memory), LocationMonitor (check every 5 min), LocationErrors,
                             LocationEndpoints (AddLocations, MapLocationEndpoints: /location-files/{id}/content|zip, /live/locations/*)
  Services/WebDav/           the WebDAV server at /dav/ (RFC 4918, class 1 and 2): WebDavEndpoints (AddWebDav, MapWebDav), WebDavHandler (.Read: GET/HEAD, PROPFIND,
                             PROPPATCH; .Write: PUT, DELETE, MKCOL, COPY/MOVE, LOCK/UNLOCK), DavStore (the tree over the business layer), DavAuthenticator (Basic),
                             DavLockManager (locks in memory), DavPath (paths, hrefs, Destination), DavIfHeader, DavXml (bodies, DavProperties, DavMultiStatus)
  Services/Accounts/         Profiles, UserAdmin, AppPasswords (WebDAV passwords: shown once, hashed)
  Services/Mail/MailService  MatSMTP through MailKit, queued in the background; off unless MATSMTP_HOST is set
  Services/Auth/             CurrentUser, PasswordService, SessionService (sessions in the DB), AccountService, ExternalSignInService (MatIdentity), AccountDataService (GDPR export/erase)
  Services/                  Ui (icons, logo, bytes), MenuService, InstanceService (numbers, settings, housekeeping), ListModels (toolbar, pager), Dates, Assets
  MatPlatform/               copy of the shared package (MatDesign, 2.4.0; tools/matplatform-copy.js): platform contract 2, bindings (MatBindings, the store in
                             /data/matplatform/bindings.json), the Connections area (<mat-connections />), MatIdentity sign-in, back-channel logout, MatSmtpSettings,
                             MAT_FRAME_ANCESTORS. Never edit here.
  Platform/                  MatDrivePlatform (tiles, settings, actions, consumes, launcher Drive + quick actions, admin widgets storage/users, health incl. office.edit;
                             publicUrl fixed while MAT_PUBLIC_URL is set), MatDriveIdentityUsers
  Controls/                  ONE code base per control: <md-field>, <md-buttons>, <md-table>, <md-tabs>/<md-tab>
  Pages/                     Drive/ (Index = the explorer: /drive, /drive/folder/{id}, /drive/shared-drive/{id}, shared, recent, starred, trash, search; Item = the tabs of an item;
                             Edit = the text editor), Drives/ (shared drives), Public/ (links /l/{token}), Account/ (+ AppPasswords), Admin/ (Users, Locations: list, Edit =
                             General, Access, Connections (<mat-connections /> + what the office editor opens, Check now; /Admin/Integrations redirects here),
                             Activity, System (+ Advanced: the address of MatDrive for other apps; fixed fields while MAT_PUBLIC_URL / MAT_INTERNAL_URL are set)),
                             Locations/ (Index = a location like the explorer: /locations/{id}/{folder…}; Edit = the
                             text editor /locations/edit/{id}?path=), About
  wwwroot/js/                util.js (t, el, icon, request, dialog, busy …), drive.js (the menu "New" on every page), explorer.js (uploads, drag & drop, menus, preview, picker: drives and storage locations),
                             locations.js (the same for a storage location: uploads, menus, pickers, properties, search, the text editor)
  wwwroot/css/app.css        accent + the styles of the explorer (prefix md-); locations.css: what the storage locations add (md-loc-, md-perm)
tests/MatDrive.Tests         unit + database tests (PlatformTests: the definition against the MatUi sprite, the app with a broker's variables; OfficeBindingTests: office.edit
                             by variables / by hand / none with a fake MatOffice, the move of the old settings)
```

## Conventions

**Database (PostgreSQL)**: tables PascalCase and singular, key always `Id` BIGINT, every record has `CreateDate/CreateUserId/UpdateDate/UpdateUserId` (filled by
`AppDbContext.SaveChanges`), `UpdateState` (0 deleted, 1 created, 2 updated) for soft delete (`User`, `Drive`, `Node`, `StorageLocation`); the trash of the drive is
`Node.TrashedDate`/`InTrash` (not UpdateState). A `Token` (uuid) for a security key (`UserSession.Token`, `ShareLink.Token`, `UploadSession.Token`).

**Security**: every query of a node goes through `DriveAccess`; something a person may not see answers 404. Every change of `/live` needs the antiforgery header
`RequestVerificationToken`. File content is served with its own CSP (sandbox) and inline only for types that cannot run script (never HTML or SVG). Secrets
(passwords of locations, the key of a binding entered by hand, kept by MatPlatform in `/data/matplatform/bindings.json`) are encrypted with DataProtection (keys in
the volume); app passwords and API keys only as hashes. The Connections area and its form (`/api/connections/form`) are for administrators only
(`MatPlatformOptions.ConnectionsAccess` = the role Admin).

**WebDAV** (`/dav/`): sign-in with HTTP Basic, the address and an app password (or the password of a local account), never the cookie; a sign-in is remembered for 2 minutes,
10 failures a minute per IP answer 429. The tree has stable English names: `My Drive/`, `Shared drives/<drive>/`, `Shared with me/<item>/` (clashing names get ` (2)`), no
trash. Everything goes through the business layer (`DriveAccess` decides: a viewer reads, a write answers 403); its activity is marked `webdav` (`ActivityLog.From`), DELETE
and `Overwrite: T` go into the trash, saves of one person within 2 minutes are one version. Locks live in memory (`DavLockManager`: at most an hour, 500 a person, gone with a
restart; a LOCK on a free name makes an empty file). The quota answers 507 (before the bytes when the length is announced), the upload limit 413. PROPFIND: depth 0 or 1, one
query for the children; PROPPATCH is answered, not stored. The Windows Explorer sends Basic only over HTTPS (or with `BasicAuthLevel=2`); `git push` over http is a client for tests.

**Storage locations** (`/locations/{id}`, administration `/Admin/Locations`): folders of the server (below `StorageRoot`, default `/storage`: the bind mounts of the NAS;
never the data folder) and shares (SMB, SFTP, FTP, WebDAV), browsed live: their files are not in the database. Who may: everyone with a role (Viewer/Editor) or the people
of the list; administrators manage them but see the files only when they are on the list; read-only and switched off are checked in `LocationAccess`, so every function of
the business layer refuses writes (404 like the drives). Every path goes through `PathGuard` (no `..`, links resolved for local folders); passwords and SFTP keys are
encrypted with DataProtection and never leave `LocationAdmin` (an empty field keeps the stored one). Errors of a server become worded messages (`Locations.Error.*`, the
server's own words in brackets), never a 500. Uploads are streamed in one request per file into a hidden temporary name (`.<guid>.matdrive-part`) and renamed when complete;
downloads stream with ranges and the CSP of `FileEndpoints.Serve`; a zip is written while it downloads. Copies between locations and to the drives go through MatDrive
(`FileService.UploadAsync`: quota and size checked); inside one location the server moves (and copies where it can: local, WebDAV COPY). Remote connections are pooled
(`ConnectionPool`, one request at a time each, closed after 2 idle minutes). `LocationMonitor` checks every active location every 5 minutes (results in memory: the
dot/cross in the menu, the list, the platform health `locations` and the action `checkLocations`). Every change is an activity `location` (target = the name of the location).

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
