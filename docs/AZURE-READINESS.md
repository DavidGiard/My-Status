# Azure readiness and approval gates

The application targets Azure, but **no Azure infrastructure is defined or provisioned** by this
repository. Local implementation approval is not deployment approval.

## Proposed architecture for review

| Service | Proposed role and smallest practical starting point | Approval considerations |
|---|---|---|
| App Service | One .NET 10 supported runtime instance; Linux Basic B1 as a cost baseline, subject to runtime/region availability | HTTPS-only, custom hostname/certificate if desired, managed identity; no multi-instance assumption |
| Azure SQL Database | Single small database; compare Basic DTU with General Purpose serverless | Basic is inexpensive but unsuitable if the approved private-network topology needs a higher tier; serverless minimum compute/resume behavior and storage cost need review |
| Key Vault | Standard vault, RSA wrapping key, managed identity access | Soft delete/purge protection; retain key versions; private endpoint costs if required |
| Application identity | App-managed username/password using ASP.NET Core Identity; one provisioned user | Secure administrative provisioning/recovery, password changes, lockout, brute-force monitoring; no MFA provided |
| Monitoring | Platform logs/alerts initially, optional Application Insights with daily cap | Do not capture passwords, activity descriptions, or credentials; review ingestion/retention costs |

These are **candidates, not selected tiers**. The code does not require containers, Kubernetes,
Redis, an AI service, a PDF service, or a storage account for encryption keys.

Prefer managed identity with SQL contained users and specific runtime DML permissions,
Key Vault Crypto Service Encryption User (or equivalent least-privilege key permissions),
and a separate short-lived deployment principal for migrations. App-managed authentication does
not require Entra app registration or Communication Services/Email Communication Services.

## Networking options needing a decision

- Lowest-complexity public SQL endpoint: tightly scoped App Service egress firewall rules, with
  **"Allow Azure services and resources to access this server" disabled**; review outbound IP changes,
  SQL public exposure and tier compatibility. This still has a public endpoint, not arbitrary-client access.
- Stronger isolation: App Service VNet integration plus SQL private endpoint and private DNS,
  disable SQL public access, and consider private Key Vault access. Review compatible App Service/SQL
  tiers, private endpoint charges, DNS and egress/NAT needs. Do not silently choose Basic DTU if the
  resulting network features require General Purpose or another supported tier.

The original plan requires reviewing networking/tier tradeoffs before selecting production topology.

## Readiness checks before infrastructure generation

Confirm these values privately, not in this public repository:

- Intended tenant/subscription, cost owner/budget, Azure region and availability, resource group/name.
- App Service OS/runtime support, service tiers, quota, SQL compute/storage/backup retention,
  availability and recovery objectives, networking topology and DNS.
- Managed identity assignments and exact runtime/migration SQL permissions.
- Secure one-account provisioning, password-manager handoff, administrative reset procedure,
  brute-force protection and session-revocation expectations.
- Canonical HTTPS hostname, AllowedHosts, secrets injection and Key Vault key retention policy.
- App Service reverse-proxy HTTPS behavior, log query redaction, alerting, and backup restore drill.

Read-only Azure authentication can identify an available subscription, but it does not identify the
**approved** subscription, region, or cost owner. Do not infer these from the CLI default account.
Keep subscription/account IDs and deployment-session notes outside this repository.

## Cost review

An **illustrative East US / USD pay-as-you-go** snapshot obtained on November 4, 2026 from the
[Azure Retail Prices API](https://prices.azure.com/api/retail/prices) returned:

| Meter | Observed rate | Example monthly calculation |
|---|---|---|
| App Service Basic Plan - Linux, B1 | $0.017/hour | 730 hours = **$12.41** |
| SQL Database Single Basic, B DTU | $0.161/day | 730/24 days = **$4.90** |
| Key Vault Standard, Operations | $0.03/10,000 operations | Usage-based; 10,000 operations = **$0.03** |
| SQL General Purpose Serverless Gen5, vCore | $0.521758/vCore-hour | If effective billed compute averages 0.5 vCore for 730 hours: **$190.44**, excluding storage |
| SQL General Purpose data stored | $0.115/GB-month | 32 GB = **$3.68** |

B1 + Basic SQL therefore have an illustrative fixed subtotal of **$17.31/month**, before
key operations, logging, networking, extra backup retention, domain registration, data
transfer, support and tax. A small public-endpoint/firewall-constrained topology might start around
**$18-20/month** with very low usage and no paid monitoring add-ons, but this is **not a complete
approved quote**. Private networking must be priced separately; matching retail meters could not
be verified in this assessment. The serverless compute example is a sensitivity calculation,
not a minimum-cost guarantee: memory billing, active/resume time, tier and auto-pause behavior matter.

The previous ACS and Microsoft sign-in proposals are superseded by app-managed username/password.
No ACS resource, sender verification, Entra app registration or client secret is required.
Lost passwords use the controlled administrative reset command, not email.

Reprice against the chosen region, billing offer and approved topology. Include idle/full-month
compute, storage/backup retention, Key Vault, private endpoints/DNS, optional NAT/egress,
and monitoring ingestion/retention. Confirm a budget and taxes/support exclusions.

Useful current references:

- https://azure.microsoft.com/pricing/calculator/
- https://azure.microsoft.com/pricing/details/app-service/linux/
- https://azure.microsoft.com/pricing/details/azure-sql-database/single/
- https://azure.microsoft.com/pricing/details/key-vault/
- https://learn.microsoft.com/azure/app-service/overview-managed-identity

## Separate gates

1. Review and explicitly approve architecture, intended subscription/region/resource group, network
   exposure, identities, tiers, account-recovery readiness, and a current cost estimate.
2. Approve **infrastructure generation**. Only then author reviewed Bicep/Terraform/azd configuration.
3. Separately approve **billable resource provisioning/deployment**. Then provision, apply controlled
   migrations, provision or update the one account securely, and test live HTTPS, username/password
   authentication, password changes, persistence, reports, logs and administrative recovery.

Until those gates are satisfied, the app is locally usable, not a deployed hosted service.
