# Operations

## Configuration and startup boundaries

Development defaults to SQLite. Set `Database__Provider=SqlServer` plus a separate development
connection string if testing SQL Server manually. Outside `Development`, the app always uses
SQL Server and fails startup if the database connection, canonical HTTPS URL, Microsoft sign-in
configuration, or Key Vault key identifier is missing.

`appsettings.Production.example.json` contains **placeholders only**, not a deployable environment.
Supply real values through App Service configuration/Key Vault references. Configure:

| Key | Purpose |
|---|---|
| `ConnectionStrings__DefaultConnection` | Azure SQL connection using managed identity; TLS enabled and certificate validation on |
| `App__PublicUrl` | Canonical HTTPS origin, with no path/query |
| `Microsoft__TenantId` | Single organizational directory GUID |
| `Microsoft__ClientId` | Application registration's client GUID |
| `Microsoft__ClientSecret` | Confidential web application's credential; inject through a Key Vault reference |
| `Microsoft__OwnerObjectId` | Authorized user's object GUID in that tenant, never an email or app object ID |
| `DataProtection__KeyIdentifier` | Versionless Key Vault RSA key URI, for wrapping persisted Data Protection keys |
| `AllowedHosts` | Exact approved application hostname(s), separated by semicolons |

Use the App Service managed identity for Azure SQL and Key Vault.
The code uses `DefaultAzureCredential`; developer credentials are for local testing only.
Do not grant subscription-wide roles or database owner to the running app.

App Service must enforce HTTPS-only and use an approved, trusted reverse-proxy/forwarded-header
configuration. Validate that the application observes HTTPS, emits secure cookies, and creates
no redirect loops. Do not blanket-trust forwarded headers from arbitrary clients. If adding another
proxy, explicitly configure trusted networks/proxies before enabling forwarded headers.
Cookies are `__Host-` scoped, Secure, HttpOnly, SameSite=Lax, with an eight-hour sliding lifetime.
Identity security stamps are revalidated within five minutes. Every authenticated request also checks
the configured tenant/object binding and its local login mapping. Legacy password cookies are rejected.

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
Configure and independently verify the intended Entra tenant and user object IDs first; see README
for registration and user-secrets setup. Provisioning creates no password. The procedure runs in a
serializable database transaction and refuses when any user exists. There is no web registration/bootstrap route.
Do not manually insert extra users into Identity tables.

To upgrade an existing password-based account, stop writes, take a recoverable database/key backup,
verify the configured tenant/object pair, and run `--link-microsoft` interactively. Confirm with **LINK**.
It preserves the sole local owner ID and all existing data, removes its password and rotates its
security stamp in the same transaction. Existing time-zone settings remain unchanged. No email matching,
automatic first-login provisioning, or replacement of an existing Microsoft binding is allowed.
If the identity must change later, revoke sessions and use a separately reviewed administrative
recovery; never change the allowlist alone and expect it to transfer data.

Time zone defaults to UTC during provisioning and is editable in Settings. Use a zone available
on the target host (IANA zone IDs are preferred for portability). Dates are calendar dates:
changing the time zone affects future defaults, not previously saved dates.

## Microsoft identity and sign-in operations

Register the production Web redirect URI as `https://<approved-host>/signin-oidc`. Validate host and
HTTPS handling through the approved proxy: the OIDC callback origin is based on the request origin,
so `AllowedHosts`, HTTPS-only and trusted forwarded-header handling must agree with the registration.
Use the single-tenant registration; no `common`/`organizations` multi-tenant authority.
The app validates issuer, signature, audience, lifetime, state/nonce/correlation, then authorizes the
immutable tenant/object pair and requires its pre-existing local binding. Email/display name are
never authorization keys. Code flow uses PKCE; tokens are not stored in the application cookie.

Microsoft manages MFA, conditional access, password policy and recovery. No local password/reset
routes or email provider remain. App **Sign out** ends only this app's session, not the user's other
Microsoft sessions. A later sign-in presents account selection but may reuse Microsoft SSO.
Rate limiting applies to the login page (eight requests per source IP per minute, including GETs).
A 429 response means wait before retrying.
Rate limiter state is process-local; use one app instance initially. Review a distributed/edge
limiter and trusted proxy client-IP handling before scaling out.

Sign-in failures emit warning **1101 / MicrosoftSignInFailure**, with only the failure type, and show
a generic error. Alert on repeated failures. Do not enable identity-library PII logging, request-body/
query-string logging or EF sensitive-data logging. Redact OIDC codes/state from proxy/access logs
and telemetry; `Referrer-Policy: no-referrer` also limits leakage. Framework logs can include sanitized
protocol diagnostics, so restrict operational log access and retention.

Keep the client secret in Key Vault references, track expiry, and rotate before it expires. Verify
the new credential using a fresh sign-in before deleting the old credential. Test organization consent,
MFA/conditional access and actual owner sign-in before go-live. Test suites use a synthetic signed-token
endpoint through real OIDC middleware; they do not prove live tenant registration or consent.
An administrator can revoke this app's sessions by removing its local external-login mapping
(all requests recheck it), or rotating the user's Identity security stamp (within five minutes).
Entra account disablement/revocation is checked on fresh Microsoft sign-in, not continuously on
every local cookie request; use explicit local revocation when immediate app access removal is needed.

## Encryption keys and backups

Data Protection keys persist in the database with a stable application discriminator `MyStatus`.
Production wraps them with the configured Key Vault key. The app identity needs key get/wrap/unwrap
permissions, not key management. Use a **versionless** key URI for rotation, retain old key versions
as long as any persisted Data Protection key may need them, enable Key Vault soft-delete/purge
protection, and monitor key expiration/access failures.

Do not change the discriminator or delete key rows during deployment. Keep the database and
wrapping-key access recoverable together. Loss of either invalidates cookies and in-flight sign-ins.
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
