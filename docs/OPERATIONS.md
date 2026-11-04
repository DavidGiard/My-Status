# Operations

## Configuration and startup boundaries

Development defaults to SQLite. Set `Database__Provider=SqlServer` plus a separate development
connection string if testing SQL Server manually. Outside `Development`, the app always uses
SQL Server and fails startup if the database connection, canonical HTTPS URL, email sender
configuration, or Key Vault key identifier is missing.

`appsettings.Production.example.json` contains **placeholders only**, not a deployable environment.
Supply real values through App Service configuration/Key Vault references. Configure:

| Key | Purpose |
|---|---|
| `ConnectionStrings__DefaultConnection` | Azure SQL connection using managed identity; TLS enabled and certificate validation on |
| `App__PublicUrl` | Canonical HTTPS origin, with no path/query, used for reset links; never derived from Host headers |
| `Email__Endpoint` | Azure Communication Services HTTPS resource endpoint |
| `Email__Sender` | Verified sender from the linked Email Communication Services domain |
| `DataProtection__KeyIdentifier` | Versionless Key Vault RSA key URI, for wrapping persisted Data Protection keys |
| `AllowedHosts` | Exact approved application hostname(s), separated by semicolons |

Use the App Service managed identity for Azure SQL, Communication Services email, and Key Vault.
The code uses `DefaultAzureCredential`; developer credentials are for local testing only.
Do not grant subscription-wide roles or database owner to the running app.

App Service must enforce HTTPS-only and use an approved, trusted reverse-proxy/forwarded-header
configuration. Validate that the application observes HTTPS, emits secure cookies, and creates
no redirect loops. Do not blanket-trust forwarded headers from arbitrary clients. If adding another
proxy, explicitly configure trusted networks/proxies before enabling forwarded headers.
Cookies are `__Host-` scoped, Secure, HttpOnly, SameSite=Lax, with an eight-hour sliding lifetime.
Identity security stamps are revalidated within five minutes (including after a password reset).

## Controlled schema migration

Never use `EnsureCreated`, drop/recreate, or implicit startup migrations on a deployed database.
Only tests create/discard databases. Use a separate deployment/migration identity with DDL permissions;
the runtime account needs only application/Identity/DataProtection table DML.

The repository includes distinct migrations/snapshots for `AppDbContext` (SQL Server) and
`LocalDbContext` (SQLite). Design-time factories deliberately use local design-only connections;
production migration commands must use the explicitly configured runtime environment or a
reviewed SQL script. Do not run `dotnet ef database update` assuming its design factory targets production.

For local development: `dotnet run -- --migrate`.
For an approved production rollout:

1. Take/confirm a recoverable backup, review the schema diff and data impact, and stop conflicting writes if required.
2. Generate an idempotent SQL Server script in a private deployment workspace:

   ```powershell
   dotnet tool restore
   dotnet ef migrations script --idempotent --context AppDbContext --output <private-script-path>
   ```

3. Review and apply it against the explicitly selected database with the deployment identity, or run the published application with `--migrate` using production configuration and a DDL-capable identity.
4. Start the web app with its restricted runtime identity; verify authentication, existing activity data, reports, and key access.
5. Prefer a reviewed roll-forward fix. Any rollback script or database restore requires its own data-loss assessment; never automatically run migration `Down` on production.

SQL Server and SQLite hierarchy triggers are deliberately authored in the initial migrations.
When changing tables with future migrations, retain/recreate these triggers. In particular,
SQLite table rebuilds can discard custom triggers. Run direct-write hierarchy tests after every
schema change, and test upgrades against a copy with existing activities and tag assignments.

## One-account administration

Run the published application with `--provision` in an administrator-controlled interactive terminal
after migration, using the target configuration and an identity allowed to write Identity tables.
Verify the email address independently; provisioning considers it confirmed.
Passwords are hidden and must not be passed in command arguments, configuration, environment
variables, repository files, or logs. The procedure runs in a serializable database transaction
and refuses when any user exists. There is no web registration/bootstrap route.
Do not manually insert extra users into Identity tables.

Configure/verify email recovery **before** relying on the application in production.
Before selecting ACS for a new deployment, review its
[retirement/onboarding restrictions](https://learn.microsoft.com/azure/communication-services/acs-retirement-and-breaking-changes-guide):
ACS Email retires September 30, 2028. Its Email FAQ currently allows new email onboarding, subject
to change at any time, and recommends migration rather than new solutions; verified domains remain
supported until retirement, with maintenance/security/critical-fix support. The October 23, 2026
initial restriction concerns phone-number acquisition for retiring telephony/SMS, not a blanket
email ban. Confirm current eligibility and obtain an explicit provider/lifecycle decision; the approved
adapter remains implemented but is not a recommendation to create a new retiring service.
For local password recovery, use Forgot password, then open the newest captured HTML file from
the private `.local\mail` directory. Delete that specific capture after use. On a lost production
password with delivery unavailable, restore the verified email service and use the standard reset
flow. Do not clear hashes or insert a default password directly into SQL. If both key access and
email recovery are lost, recover the original keys/identity configuration or perform a separately
reviewed administrative recovery using ASP.NET Core Identity `UserManager`.

Time zone defaults to UTC during provisioning and is editable in Settings. Use a zone available
on the target host (IANA zone IDs are preferred for portability). Dates are calendar dates:
changing the time zone affects future defaults, not previously saved dates.

## Password reset and email operations

Reset links use Identity Data Protection tokens, last 30 minutes, and become invalid after
a successful reset changes the security stamp. Neither known nor unknown addresses receive an
account-existence signal in the response. Rate limiting applies to login/recovery/reset endpoints
(eight requests per source IP per minute, including GETs); account lockout follows five failed
password attempts for 15 minutes. A 429 response means wait before retrying.
Rate limiter state is process-local; use one app instance initially. Review a distributed/edge
limiter and trusted proxy client-IP handling before scaling out.

Email failures emit operational error **1001 / ResetEmailFailure**, without token, recipient, or
provider exception payloads. Alert on that event and repeated HTTP failures. The generic recovery
response does not claim delivery, and tells the user to contact the operator if mail does not arrive.
Do not enable request-body/query-string logging, EF sensitive-data logging, or token-containing
reset URL telemetry. Review App Service access logs and any telemetry provider for query redaction
before enabling them; application `Referrer-Policy: no-referrer` prevents reset URL referral leakage.

ACS delivery acceptance does not prove inbox delivery. Verify sender linkage/domain DNS, SPF/DKIM,
real inbox and spam-folder delivery, quotas, and bounce/suppression handling before go-live.
Tests replace the email sender and never contact ACS.

## Encryption keys and backups

Data Protection keys persist in the database with a stable application discriminator `MyStatus`.
Production wraps them with the configured Key Vault key. The app identity needs key get/wrap/unwrap
permissions, not key management. Use a **versionless** key URI for rotation, retain old key versions
as long as any persisted Data Protection key may need them, enable Key Vault soft-delete/purge
protection, and monitor key expiration/access failures.

Do not change the discriminator or delete key rows during deployment. Keep the database and
wrapping-key access recoverable together. Loss of either invalidates cookies/reset links.
Windows development DPAPI keys cannot be moved to another user/machine; start a new isolated
development database if that identity is intentionally replaced.

Azure SQL point-in-time restore retention and recovery objectives must be explicitly selected
and tested. Perform a restore drill into a separate protected database, verify owner data and
key decryption, then dispose of the restore copy under the approved retention policy.
Backups/database exports and logs may contain private activity data and never belong in the
public repository. For SQLite, stop the app before an offline copy, or use SQLite's backup API;
do not copy only a live `.db` while ignoring WAL state.

The app intentionally does not store a report history or reset inclusion flags. Reports can overlap.
Use browser Print / Save as PDF to retain a manager-ready snapshot outside the repository.
