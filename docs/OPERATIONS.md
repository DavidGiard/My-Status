# Operations

## Configuration and startup boundaries

Development defaults to SQLite. Set `Database__Provider=SqlServer` plus a separate development
connection string if testing SQL Server manually. Outside `Development`, the app always uses
SQL Server and requires a connection string, canonical HTTPS URL and Key Vault key identifier.
`appsettings.Production.example.json` contains placeholders only, not a deployable environment.

| Key | Purpose |
|---|---|
| `ConnectionStrings__DefaultConnection` | Azure SQL connection using managed identity, TLS and certificate validation |
| `App__PublicUrl` | Canonical HTTPS origin, with no path/query |
| `DataProtection__KeyIdentifier` | Versionless Key Vault RSA key URI protecting persisted Data Protection keys |
| `AllowedHosts` | Exact approved application hostname(s), separated by semicolons |

Use managed identity for Azure SQL and Key Vault. `DefaultAzureCredential` developer credentials
are only for local testing. Do not grant the runtime subscription-wide roles or database owner.
App Service must enforce HTTPS-only and use approved trusted-proxy/forwarded-header configuration.
Verify HTTPS is observed correctly and there are no redirect loops. Never blanket-trust forwarded
headers. Cookies are `__Host-` scoped, Secure, HttpOnly, SameSite=Lax, with an eight-hour sliding lifetime.

## Controlled schema migration

Never use `EnsureCreated`, drop/recreate, or implicit startup migrations on deployed databases.
Only tests create/discard databases. Use a separate deployment identity with DDL permissions;
runtime needs only application/Identity/DataProtection table DML.

The repository includes distinct migrations/snapshots for `AppDbContext` (SQL Server) and
`LocalDbContext` (SQLite). Design-time factories use local design-only connections. Do not assume
`dotnet ef database update` uses production. For local development: `dotnet run -- --migrate`.

For approved production rollout:

1. Confirm a recoverable backup; review schema/data impact and stop conflicting writes if required.
2. Generate an idempotent SQL Server script in a private deployment workspace:

   ```powershell
   dotnet tool restore
   dotnet ef migrations script --idempotent --context AppDbContext --output <private-script-path>
   ```

3. Review/apply it to the explicitly selected database with the deployment identity, or run the published application with `--migrate` using production configuration and a DDL-capable identity.
4. Start with restricted runtime identity; verify authentication, existing activity data, reports and key access.
5. Prefer reviewed roll-forward fixes. Database restore or rollback requires a separate data-loss assessment; never automatically run migration `Down` on production.

Preserve the hierarchy triggers authored in both providers' initial migrations. SQLite table rebuilds
can discard custom triggers. Run direct-write hierarchy tests and upgrade tests with existing data.
Switching from Microsoft to local passwords needs no schema change.

## Single-account administration and recovery

Run `--provision` after migrations in an administrator-controlled terminal. It prompts for username
and hidden password and refuses if any user already exists. There is no public registration/bootstrap
route, default account or committed password. Identity salts/hashes passwords and requires 12+
characters, upper/lowercase, digit and symbol. Keep credentials in a password manager.

To replace credentials on the sole existing account, stop writes and back up the database/keys.
Run `--set-password`, confirm **REPLACE**, then enter username and new password. It preserves the
local owner ID, activities, categories, tags and time zone; removes external login mappings; resets
lockout; and rotates the security stamp in a serializable transaction. Invalid input rolls back.
More than one existing user is an error requiring administrative review, never automatic deletion.
The CLI also accepts trusted redirected standard input: confirmation when replacing, username,
password, one per line. Never put passwords in arguments, environment settings, repository or logs.

This is also the lost-password recovery procedure. No email sender, ACS dependency, Entra registration,
Microsoft SSO or self-service forgot-password flow remains. Old Entra registrations/credentials, if
previously created, can be removed separately after confirming no other application uses them.
No tenant-side objects are automatically deleted by this application.

Users can change a known password under **Settings > Change password**, supplying their current
password. The current session is refreshed; other sessions are revoked when their security stamp
is revalidated (within five minutes). Administrative reset also revokes sessions within five minutes.
Sign out deletes this application's session cookie.

Five failed password attempts lock the account for 15 minutes. Generic login errors do not disclose
whether a username exists or is locked. Login/password-change requests are limited to eight per
source IP per minute per endpoint policy, including GETs; a 429 means wait. Limiter state is process-local: use one
instance initially, review edge/distributed limits and trusted client-IP handling before scaling.
Restrict administrative shell and DB access; anyone with those permissions can replace credentials.

Do not enable request-body logging, EF sensitive-data logging or credential-bearing telemetry.
Restrict operational logs and alert on repeated authentication failures/lockouts and HTTP errors.
No passwords should appear in access logs. Public hosting requires a reviewed brute-force and
account-recovery procedure; local-password authentication does not provide MFA.

Time zone defaults to UTC for new accounts and is editable in Settings. Prefer portable IANA IDs.
Calendar dates already stored do not change when the time zone changes.

## Encryption keys and backups

Data Protection keys persist in the DB with stable discriminator `MyStatus`. Production wraps them
with a Key Vault key. Grant get/wrap/unwrap, not key management. Use a versionless key URI, retain
old key versions, enable soft-delete/purge protection and monitor expiry/access failures.

Do not change the discriminator or delete key rows during deployment. Keep DB and wrapping-key
access recoverable together. Loss invalidates cookies. Windows DPAPI keys cannot move between
users/machines. Do not recreate the real data DB just to resolve key access; review recovery first.

Select Azure SQL point-in-time retention and recovery objectives explicitly. Restore into a separate
protected DB to verify owner data/key decryption, then dispose under approved retention policy.
Backups, exports and logs contain private data and never belong in the public repository.
For SQLite, stop the app before an offline backup or use SQLite's backup API; do not copy only
the `.db` while ignoring live WAL state. Protect the backup and any credential handoff file with
OS permissions; delete a plaintext credential handoff after importing into a password manager.

The app does not store report history or reset inclusion flags. Reports can overlap.
Use browser Print / Save as PDF for snapshots outside the repository.
