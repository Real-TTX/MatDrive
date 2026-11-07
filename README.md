<div align="center">

<img src="docs/logo/matdrive-mark-512.png" width="96" alt="MatDrive" />

# MatDrive

**Office in the browser – four apps in one.**

Docs, Sheets, Slides and Base: write, calculate, present and organise, with Word, Excel and PowerPoint files.
Self-hosted, one container plus PostgreSQL.

</div>

---

## The apps

| App | Like | Files |
|---|---|---|
| **Docs** | Word | `.docx`, HTML, Markdown, text, the own `.matdoc` |
| **Sheets** | Excel | `.xlsx`, `.csv`, the own `.matsheet` |
| **Slides** | PowerPoint | `.pptx`, the own `.matslides` |
| **Base** | Access | the own `.matbase`; tables from CSV / Excel |

All four share one frame: the title bar in the colour of the app, the ribbon with its tabs, **File** (info, new, open, save a copy, download as, print,
share, version history), autosave with conflict handling, a version history, sharing with people and through a link, the trash, offline use.

## Quick start

```yaml
name: matdrive

services:
  web:
    image: ghcr.io/real-ttx/matdrive:latest
    restart: unless-stopped
    ports:
      - "4810:4810"
    volumes:
      - web-data:/data
    depends_on:
      db:
        condition: service_healthy

  db:
    image: postgres:17
    restart: unless-stopped
    environment:
      POSTGRES_DB: matdrive
      POSTGRES_USER: matdrive
      POSTGRES_PASSWORD: matdrive
    volumes:
      - db-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U matdrive -d matdrive"]
      interval: 5s
      timeout: 5s
      retries: 20

volumes:
  web-data:
  db-data:
```

Open **http://localhost:4810**; the first account that registers is the administrator.

| Tag | Built from |
|---|---|
| `ghcr.io/real-ttx/matdrive:latest` | `main` (release) |
| `ghcr.io/real-ttx/matdrive:nightly` | `dev` (development) |

## Connected apps (MatDrive)

MatDrive opens its Word, Excel, PowerPoint and MatDrive files in MatDrive. In MatDrive: *Administration → Connected apps → Connect an app* makes an API
key (shown once). In MatDrive: *Administration → Integrations → MatDrive*, the address of MatDrive and the key. Details in [docs/EDITORS.md](docs/EDITORS.md)
and in the `HostEndpoints` (`/api/v1/host/formats`, `/api/v1/host/new`, `/api/v1/host/sessions`).

## Mat platform

`/api` (description), `/api/health`, `/api/state`, `/api/configure`, `/api/actions` after the platform contract of MatDesign (`MAT_API_KEY` or a token of
MatIdentity); sign-in through MatIdentity (`MATIDENTITY_AUTHORITY`, `MATIDENTITY_CLIENT_ID`, `MATIDENTITY_CLIENT_SECRET`, `MATIDENTITY_INTERNAL_URL`),
mail through MatSMTP (`MATSMTP_HOST` …). All optional, set by Mattiverse.

## Development

```bash
docker compose -f docker-compose.local.yml up -d --build    # http://localhost:4810, pgAdmin http://localhost:4811
dotnet build MatDrive.slnx
dotnet test MatDrive.slnx
```

## Branches & versioning

| Branch | Purpose | Version |
|---|---|---|
| `main` | Release | `<major>.<minor>.<build>-<yyyyMMdd>` |
| `dev` | Development | `nightly-<build>-<yyyyMMdd>` |
| local | – | `local-<yyyyMMdd>` |
