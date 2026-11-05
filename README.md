# My Status

A private, single-account activity tracker and manager-ready status report application.
Built with **.NET 10 LTS**, ASP.NET Core Razor Pages, ASP.NET Core Identity, and EF Core.
SQLite is used only for local development; production targets Azure SQL and Azure App Service.
Public source does not mean public access to activities.

## What it does

- Activity create/view/edit/confirmed deletion, four fixed statuses, one required category or subcategory, reusable tags, and report-inclusion flags.
- Search, pagination, combinable activity/completion-date filters, parent-category filtering including children, and **any-selected-tag** filtering.
- Two-level category management with normalized sibling uniqueness; tag management with normalized uniqueness. Referenced categories cannot be deleted. Deleting a tag removes assignments, not activities.
- Inclusive completion-date status reports containing **only flagged Complete activities**, grouped and sorted by category/subcategory, with email-friendly copy (manual fallback), print, and browser Save as PDF.
- App-managed username/password sign-in, one-account administrative provisioning, password changes, time-zone settings, protected encryption keys, and optimistic concurrency.

There is no public registration, Microsoft sign-in, automatic manager email, or automatic database
migration on web startup. Password recovery is an administrator-controlled reset, not email.

## Run locally (PowerShell)

Install the SDK selected by `global.json` (latest patch roll-forward is allowed). From this checkout:

```powershell
dotnet restore
dotnet tool restore
dotnet dev-certs https --trust
dotnet run -- --migrate
dotnet run -- --provision
dotnet run
```

`--provision` prompts for a username and hidden password and refuses if an account already exists.
Passwords require at least 12 characters with uppercase, lowercase, a digit, and a symbol.
No default credentials are included in source. Passwords are salted and hashed by ASP.NET Core Identity,
never stored as plaintext in the database. The new user's time zone defaults to UTC; change it in Settings.

Open **https://localhost:7143** and sign in. The launch profile sets `Development` and uses the separate
`.local\my-status.db` SQLite database. No Microsoft app registration or email provider is required.
Never use development mode on a hosted/public server.

If upgrading an existing installation or recovering a password, stop the app, back up the database/keys,
then run `dotnet run -- --set-password`. Confirm with **REPLACE**, then supply the username and new
password. This preserves the sole existing user's ID, activities, categories, tags and time zone;
removes any old external-login mappings; clears lockout; and invalidates previous sessions within
five minutes. It refuses to modify a database containing more than one user. No database recreation
or schema change is needed for this authentication change.

After signing in, use **Settings > Change password** to replace a generated password.
Keep credentials in a password manager and delete any operator-created plaintext credential handoff
file after saving it. The CLI accepts redirected standard input for trusted local administration
(confirmation if replacing, username, password, each on its own line); never pass passwords in command
arguments, commit them, or record the input in logs.

Local configuration belongs in user-secrets or environment variables, not committed files.
The development database, credentials, test data, and generated artifacts under `.local` are ignored
by Git and excluded from publish output. Restrict `.local` to your OS account. Windows development
Data Protection keys use current-user DPAPI; other development platforms persist unencrypted keys
in their **local-only** database. Production requires Key Vault protection.

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
dotnet build tests\MyStatus.Tests.csproj --configuration Release --warnaserror -p:RestoreLockedMode=true
dotnet test tests\MyStatus.Tests.csproj --configuration Release --no-build
```

The default test run covers real SQLite migrations, domain invariants, owner isolation, HTTP handlers,
anti-forgery, password hashing/login/lockout/rate limiting, password changes and administrative reset
rollback/session invalidation, protected-key persistence, filtering, and exact report membership.
It uses disposable synthetic accounts/databases, no real credentials or external identity provider.

Optional full verification on Windows with SQL Server LocalDB and Chromium:

```powershell
dotnet build tests\MyStatus.Tests.csproj --configuration Release
$env:PLAYWRIGHT_SKIP_BROWSER_GC = "1"
pwsh tests\bin\Release\net10.0\playwright.ps1 install chromium
$env:MY_STATUS_SQLSERVER_TEST = "1"
$env:MY_STATUS_BROWSER_TEST = "1"
dotnet test tests\MyStatus.Tests.csproj --configuration Release --no-build
```

The SQL Server test creates/removes a uniquely named local test database. Chromium exercises real
login, password change, activity/category/tag/report flows, safe text, clipboard fallback, print and
a 375-pixel viewport. Test-only screenshots/PDF live under `.local` relative to the test process.
These two tests are explicitly skipped without their flags.

## Architecture and operations

One web project, with small shared domain/report services; no separate SPA or API. Owner checks
are repeated in request queries and association validation, with composite foreign keys preventing
cross-owner category/tag assignments. Database checks enforce lengths and completion/status
consistency, category triggers enforce two levels, and GUID concurrency tokens protect mutations.

See [operations](docs/OPERATIONS.md) for controlled migrations, account recovery, key rotation and
backups, and [Azure readiness](docs/AZURE-READINESS.md) for the **separate architecture,
infrastructure-generation, and provisioning approval gates**.
