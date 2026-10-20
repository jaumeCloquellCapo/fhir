# Configuration

Settings live in `src/FhirServer/appsettings.json`, with optional overrides via `appsettings.Development.json`, environment variables, or [.NET user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets).

Environment variable form uses `__` for nesting, e.g. `ConnectionStrings__Fhir=...`.

## Store (PostgreSQL)

| Key | Description |
|-----|-------------|
| `ConnectionStrings:Fhir` | Npgsql connection string |

```json
{
  "ConnectionStrings": {
    "Fhir": "Host=localhost;Port=5432;Database=fhir;Username=fhir;Password=fhir"
  }
}
```

Local Compose defaults (`make db-up`) match the credentials above. **Do not** reuse them outside a disposable local machine. Migrations run automatically on startup.

## Auth (basic + SMART Bearer)

| Key | Default | Description |
|-----|---------|-------------|
| `Auth:Enabled` | `true` | Require HTTP Basic **or** SMART Bearer on protected routes (`false` only for disposable local demos) |
| `Auth:Username` | — | Basic username |
| `Auth:Password` | — | Basic password — **set via secrets in any shared environment** |
| `Auth:BasicScopes` | `system/*.cruds` | SMART scopes applied to Basic credentials (same enforcement as Bearer) |

## SMART

| Key | Default | Description |
|-----|---------|-------------|
| `Smart:Enabled` | `true` | Advertise SMART discovery |
| `Smart:EnableDcr` | `false` | When `true`, expose `POST /auth/register` (patient/user scopes only; no `system/*`) |
| `Smart:RequireAuthorizeLogin` | same as `Auth:Enabled` | When `true`, `/auth/authorize` requires HTTP Basic before issuing a code |
| `Smart:TokenLifetimeMinutes` | `60` | Access token lifetime |
| `Smart:AuthCodeLifetimeMinutes` | `5` | Authorization code lifetime |
| `Smart:Clients` | (see appsettings) | Array of confidential clients |

Each client entry:

| Key | Description |
|-----|-------------|
| `ClientId` / `ClientSecret` | Confidential client credentials — **change in shared environments** (empty secret → client ignored) |
| `AllowedScopes` | Space-separated allow-list; token requests are intersected with this set |
| `GrantTypes` | e.g. `client_credentials`, `authorization_code` |
| `RedirectUris` | Allowed redirect URIs for authorize |
| `OrganizationId` | Optional ORGBAC tenant (`hospital-a` or `Organization/hospital-a`). Tokens include `organization`; CRUD/search/history are filtered to that tenant |

Legacy single-client keys `Smart:ClientId`, `Smart:ClientSecret`, `Smart:Scopes`, and `Smart:OrganizationId` are still accepted when `Smart:Clients` is empty (secret required).

## Multi-tenancy (ORGBAC MVP)

| Key | Description |
|-----|-------------|
| `Smart:Clients[].OrganizationId` | Scopes a confidential client to one Organization id |
| `Auth:OrganizationId` | Optional; when set, HTTP Basic is also tenant-scoped (default Basic is **unrestricted** admin) |

Envelopes store `tenant_id` (empty when the writer was unrestricted). Postgres RLS policies filter on `current_setting('app.tenant_id')` (set per store transaction via `SET LOCAL`). Resource ids remain globally unique across tenants.

`POST /auth/register` is off unless `Smart:EnableDcr=true`. Registered clients are ephemeral (process memory) and cannot request `system/*` or `client_credentials`.

Auth is **on** by default. Change `Auth:Password` / SMART client secrets before any shared use. Set `Auth:Enabled=false` only for disposable local demos.

## Subscriptions

| Key | Default | Description |
|-----|---------|-------------|
| `Subscriptions:AllowPublicHooks` | `false` | Whether `/_hooks/{id}` is reachable without credentials |
| `Subscriptions:AllowRemoteHooks` | `false` | When `true`, rest-hook delivery may POST to **loopback** URLs only (SSRF guard). In-process `/_hooks/` sinks always work |

## Audit

| Key | Default | Description |
|-----|---------|-------------|
| `Audit:Enabled` | `false` | Persist BALP-oriented `AuditEvent` resources for REST ops |

## Logging & hosting

Standard ASP.NET Core `Logging` and `AllowedHosts` sections apply. The development listen URL is typically **http://localhost:5050** (see `launchSettings.json` / `dotnet run` output).

## Security notes for operators

- Prefer strong credentials and TLS in non-local deployments (defaults use `change-me` / Compose `fhir`)
- Keep `Smart:EnableDcr=false` unless you intentionally need ephemeral patient-app registration
- Keep `Subscriptions:AllowRemoteHooks=false` unless you understand the SSRF surface
- Treat this MVP as a foundation: add threat modeling, backups, and compliance controls before handling real PHI
