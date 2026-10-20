# Configuration

Settings live in `FhirServer/appsettings.json`, with optional overrides via `appsettings.Development.json`, `.env.dev` / `.env`, environment variables, or [.NET user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets).

Environment variable form uses `__` for nesting, e.g. `ConnectionStrings__Fhir=...`. At startup the server loads `.env.dev` then `.env` from the repo root (and content root) without overwriting variables already set in the process.

## Store (PostgreSQL)

| Key | Description |
|-----|-------------|
| `ConnectionStrings:Fhir` | Npgsql connection string (`ConnectionStrings__Fhir` in env files) |

Local defaults are in [`.env.dev`](../.env.dev) (matches `make db-up`). Public template: [`.env.example`](../.env.example) — copy to a gitignored `.env` for machine-specific secrets. **Do not** reuse Compose credentials outside a disposable local machine. Migrations run automatically on startup.

## Auth (basic + SMART Bearer)

| Key | Default | Description |
|-----|---------|-------------|
| `Auth:Username` | — | Basic username |
| `Auth:Password` | — | Basic password — **set via secrets in any shared environment** |
| `Auth:BasicScopes` | `system/*.cruds` | SMART scopes applied to Basic credentials (same enforcement as Bearer) |

## SMART

| Key | Default | Description |
|-----|---------|-------------|
| `Smart:Enabled` | `true` | Advertise SMART discovery |
| `Smart:EnableDcr` | `false` | When `true`, expose `POST /auth/register` (patient/user scopes only; no `system/*`) |
| `Smart:RequireAuthorizeLogin` | `true` | When `true`, `/auth/authorize` requires HTTP Basic before issuing a code |
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

## Multi-tenancy (ORGBAC)

| Key | Description |
|-----|-------------|
| `Smart:Clients[].OrganizationId` | Scopes a confidential client to one Organization id |
| `Auth:OrganizationId` | Optional; when set, HTTP Basic is also tenant-scoped (default Basic is **unrestricted** admin) |

Envelopes store `tenant_id` and `tenant_mode` (`owned` \| `shared` \| `system-shared`). Empty `tenant_id` when the writer was unrestricted or the resource is system-shared. Organization trees use `Organization.partOf` and `organization_closure`. Prefer `/Organization/{orgId}/fhir/...` for hierarchical access; root routes keep exact-tenant filtering for org-bound tokens. RLS uses `app.tenant_id` + `app.orgbac_mode` (`exact` \| `hierarchy`). Resource ids remain globally unique across tenants.

`POST /auth/register` is off unless `Smart:EnableDcr=true`. Registered clients are ephemeral (process memory) and cannot request `system/*` or `client_credentials`.

Auth is always required on protected routes. Change `Auth:Password` / SMART client secrets before any shared use.

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
