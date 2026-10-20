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
| `Auth:Enabled` | `false` | When `true`, require HTTP Basic **or** SMART Bearer |
| `Auth:Username` | — | Basic username |
| `Auth:Password` | — | Basic password — **set via secrets in any shared environment** |

## SMART

| Key | Default | Description |
|-----|---------|-------------|
| `Smart:Enabled` | `true` | Advertise SMART discovery (tokens always available) |
| `Smart:ClientId` | `fhir-client` | OAuth client id for `client_credentials` |
| `Smart:ClientSecret` | `change-me` | OAuth client secret — **change in shared environments** |
| `Smart:Scopes` | `system/*.read system/*.write openid fhirUser` | Default scopes |
| `Smart:TokenLifetimeMinutes` | `60` | Access token lifetime |

Open-source defaults keep auth off for local demos. Never commit real credentials.

## Audit

| Key | Default | Description |
|-----|---------|-------------|
| `Audit:Enabled` | `false` | Persist BALP-oriented `AuditEvent` resources for REST ops |

## Logging & hosting

Standard ASP.NET Core `Logging` and `AllowedHosts` sections apply. The development listen URL is typically **http://localhost:5050** (see `launchSettings.json` / `dotnet run` output).

## Security notes for operators

- Prefer strong credentials and TLS in non-local deployments
- Enable auth (or put the API behind a gateway) before exposing the port
- Treat this MVP as a foundation: add threat modeling, backups, and compliance controls before handling real PHI
