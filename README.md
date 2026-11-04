# My Status

A private, single-account activity tracker and manager-ready status report application.
Built with **.NET 10 LTS**, ASP.NET Core Razor Pages, Identity, and EF Core.
SQLite is used only for local development; production targets Azure SQL and Azure App Service.
Public source does not mean public access to activities.

## What it does

- Activity create/view/edit/confirmed deletion, four fixed statuses, one required category or subcategory, reusable tags, and report-inclusion flags.
- Search, pagination, combinable activity/completion-date filters, parent-category filtering including children, and **any-selected-tag** filtering.
- Two-level category management with normalized sibling uniqueness; tag management with normalized uniqueness. Referenced categories cannot be deleted. Deleting a tag removes assignments, not activities.
- Inclusive completion-date status reports containing **only flagged Complete activities**, grouped and sorted by category/subcategory, with email-friendly copy (manual fallback), print, and browser Save as PDF.
- Private email/password sign-in, secure one-account administrative provisioning, time-zone settings, password recovery, persistent protected encryption keys, and optimistic concurrency.

There is no public registration, manager login, automatic manager email, or automatic database migration on web startup.

## Run locally (PowerShell)

Install the SDK selected by `global.json` (10.0.403; latest patch roll-forward is allowed).
From this checkout:

```powershell
dotnet restore
dotnet tool restore
dotnet dev-certs https --trust
dotnet run -- --migrate
dotnet run -- --provision
dotnet run
```

`--provision` must run in a real interactive terminal. It prompts for the email, time zone
(blank means UTC), and a hidden password. It refuses to create any account if one already exists.
Use a unique password with at least 12 characters, upper/lowercase letters, a digit, and a symbol.
No default account/password is provided. The operator must verify they control the supplied email;
administrative provisioning marks it confirmed.

Open **https://localhost:7143**. The launch profile sets `Development`, uses the separate
`.local\my-status.db` SQLite database, and captures reset emails to gitignored `.local\mail`.
Never use development mode on a hosted/public server. No Azure resource or real email sender is
needed for local development.

If running without the launch profile, explicitly set `ASPNETCORE_ENVIRONMENT=Development`.
The development fallback for `App:PublicUrl` is `https://localhost:7143`; configure it if the port changes:

```powershell
dotnet user-secrets set "App:PublicUrl" "https://localhost:7143"
```

Local configuration belongs in user-secrets or environment variables, not committed files.
The development database, reset captures, test data, and generated artifacts are ignored by Git.
Private `.local` files, tests, certificates, and environment-specific configuration are also excluded
from published output; production configuration must be injected separately.
Restrict `.local` to your OS account: reset captures are credential-equivalent until used/expired.
Windows development Data Protection keys use current-user DPAPI; other development platforms
persist unencrypted keys in their **local-only** database. Production requires Key Vault protection.

## Use

1. Create a category in **Categories**; optionally create a subcategory under it. Names are unique within each parent, ignoring case, surrounding whitespace, and Unicode compatibility differences.
2. Create tags as needed. Add an activity with its planned/activity date and plain-text description (maximum 2,000 characters). Category/tag names allow 100 characters.
3. Mark work **Complete**. Leave completion date blank to use today in your configured time zone, or enter a backdated date. Reopening/cancelling clears completion date; re-completing establishes a new editable date.
4. Generate a **Status report** using completion-date boundaries. Only checked report flags on Complete activities qualify. Preview, copied text, and print share the same report result. Reports never reset flags.

Concurrent edits/deletes show an error instead of silently overwriting. Preserve your draft text and
reload the saved version before trying again. All descriptions and names are rendered as text,
including any markup-like content.

## Verify

```powershell
dotnet build --configuration Release
dotnet test tests\MyStatus.Tests.csproj
```

The default test run covers real SQLite migrations, domain invariants, owner isolation, HTTP
handlers, anti-forgery, Identity lockout/rate limiting, password resets (including reuse/expiry),
protected-key persistence across host restarts, filtering, and exact report membership.
It sends **no real email** and uses disposable synthetic accounts/databases.

Optional full verification on Windows with SQL Server LocalDB and Chromium:

```powershell
dotnet build tests\MyStatus.Tests.csproj
$env:PLAYWRIGHT_SKIP_BROWSER_GC = "1"
pwsh tests\bin\Debug\net10.0\playwright.ps1 install chromium
$env:MY_STATUS_SQLSERVER_TEST = "1"
$env:MY_STATUS_BROWSER_TEST = "1"
dotnet test tests\MyStatus.Tests.csproj
```

The SQL Server test creates and removes a uniquely named **local test database**. The browser test
starts a temporary HTTPS Kestrel server with synthetic data, exercises activity/category/tag/report
flows, checks safe text rendering, clipboard fallback, print controls, and a 375-pixel viewport,
and saves test-only screenshots/PDF under `.local` relative to the test process directory.
The two optional tests are explicitly skipped without their environment flags.

## Architecture and operations

One web project, with small shared domain/report services; no separate SPA or API. Owner checks
are repeated in request queries and association validation, with composite foreign keys preventing
cross-owner category/tag assignments. Database checks enforce lengths and completion/status
consistency, category triggers enforce two levels, and GUID concurrency tokens protect mutations.

See [operations](docs/OPERATIONS.md) for controlled migrations, account recovery, key rotation,
backups and production configuration, and [Azure readiness](docs/AZURE-READINESS.md) for the
**separate architecture, infrastructure-generation, and provisioning approval gates**.
