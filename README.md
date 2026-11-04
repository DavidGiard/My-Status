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
- Work/school Microsoft Entra ID sign-in restricted to one explicitly linked identity, time-zone settings, persistent protected encryption keys, and optimistic concurrency. Microsoft manages passwords, recovery and MFA.

There is no public registration, manager login, automatic manager email, or automatic database migration on web startup.

## Run locally (PowerShell)

Install the SDK selected by `global.json` (10.0.403; latest patch roll-forward is allowed).
From this checkout:

```powershell
dotnet restore
dotnet tool restore
dotnet dev-certs https --trust
dotnet run -- --migrate
```

### Configure Microsoft sign-in

In the intended work/school tenant's **Microsoft Entra admin center > App registrations**,
create a registration for **Accounts in this organizational directory only (Single tenant)**.
Add a **Web** redirect URI of `https://localhost:7143/signin-oidc`.
Do not enable implicit grants or public-client flows. This app requests only `openid profile`;
it does not call Microsoft Graph. Remove any default delegated Graph `User.Read` permission
if present and unused. Your organization may require administrator approval for registration/consent.

Record the **Directory (tenant) ID**, **Application (client) ID**, and your user's **Object ID**
from that tenant's Users page (not the app registration's object ID).
Create a client secret and store its **value**, not its secret ID, in local user-secrets:

```powershell
dotnet user-secrets set "Microsoft:TenantId" "<directory-tenant-guid>"
dotnet user-secrets set "Microsoft:ClientId" "<application-client-guid>"
dotnet user-secrets set "Microsoft:ClientSecret" "<client-secret-value>"
dotnet user-secrets set "Microsoft:OwnerObjectId" "<your-user-object-guid-in-this-tenant>"
```

Never put credentials in the repository or paste them into chat. Local user-secrets are outside
the repository but are not an encrypted vault; restrict access to your OS account. Rotate the secret
before expiry. Microsoft personal accounts are not enabled by this configuration.

For a **new, empty application database**, run `dotnet run -- --provision` in an administrator-controlled
interactive terminal. It creates a passwordless local owner record bound to the exact tenant/object
pair and prompts for the initial time zone (blank means UTC). It refuses if any account exists.

For an **existing installation**, stop the app and back up the database/keys first. Double-check the
configured tenant and user object ID, then run `dotnet run -- --link-microsoft`. Type **LINK** at the
confirmation prompt. This preserves the existing owner ID and all activities/categories/tags/time-zone
settings, removes the old password and invalidates prior sessions. It never links by email and refuses
to replace an already linked account. No schema change or database recreation is needed.

Then run:

```powershell
dotnet run
```

Open **https://localhost:7143**. The launch profile sets `Development`, uses the separate
`.local\my-status.db` SQLite database. Select **Sign in with Microsoft** and authenticate on Microsoft's
site; this app never asks for your Microsoft password. Until configuration is complete the button is
disabled, rather than falling back to local passwords. No email sender or ACS resource is needed.
Never use development mode on a hosted/public server.

If running without the launch profile, explicitly set `ASPNETCORE_ENVIRONMENT=Development`.
The development fallback for `App:PublicUrl` is `https://localhost:7143`; configure it if the port changes:

```powershell
dotnet user-secrets set "App:PublicUrl" "https://localhost:7143"
```

Local configuration belongs in user-secrets or environment variables, not committed files.
The development database, test data, and generated artifacts are ignored by Git.
Private `.local` files, tests, certificates, and environment-specific configuration are also excluded
from published output; production configuration must be injected separately.
Restrict `.local` to your OS account; it contains private data and protected authentication keys.
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
handlers, anti-forgery, sign-in rate limiting, real OpenID Connect middleware against a test-only signed-token endpoint,
protected-key persistence across host restarts, filtering, and exact report membership.
It rejects incorrect tenant/owner, issuer, audience, signature, nonce and expired tokens, uses
disposable synthetic accounts/databases, and never contacts Microsoft or sends email.

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
