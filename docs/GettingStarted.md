# Getting started

Run FhirServer locally and call the FHIR API in a few minutes.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://docs.docker.com/get-docker/) (if you use Postgres via Compose)
- Optional: [jq](https://jqlang.github.io/jq/) for pretty JSON in the shell

On Homebrew macOS you may need:

```bash
export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
export PATH="/opt/homebrew/opt/dotnet/bin:$PATH"
```

## Option A — PostgreSQL (recommended)

1. Start the database:

   ```bash
   make db-up
   ```

   Compose starts Postgres 18 on `localhost:5432` with user/db/password `fhir` / `fhir` / `fhir` (local only—change for any shared environment).

2. Point the API at Postgres in `src/FhirServer/appsettings.json` (or environment / user secrets):

   ```json
   {
     "Store": { "Provider": "postgres" },
     "ConnectionStrings": {
       "Fhir": "Host=localhost;Port=5432;Database=fhir;Username=fhir;Password=fhir"
     }
   }
   ```

3. Run the server:

   ```bash
   dotnet run --project src/FhirServer
   ```

4. Open **http://localhost:5050** — try `GET /metadata` and `GET /health`.

5. Explore the API in **Swagger UI**: [http://localhost:5050/swagger](http://localhost:5050/swagger)  
   OpenAPI JSON: `http://localhost:5050/swagger/v1/swagger.json`

## Option B — SQLite (zero Docker)

Default configuration uses SQLite at `src/FhirServer/data/fhir.db`:

```json
{
  "Store": { "Provider": "sqlite" },
  "ConnectionStrings": {
    "Fhir": "Data Source=data/fhir.db"
  }
}
```

```bash
dotnet run --project src/FhirServer
```

## Verify with curl

```bash
# CapabilityStatement
curl -s http://localhost:5050/metadata | jq .

# Create a Patient
curl -s -X POST http://localhost:5050/Patient \
  -H 'Content-Type: application/fhir+json' \
  -d '{"resourceType":"Patient","name":[{"family":"Garcia","given":["Ana"]}],"gender":"female"}' \
  | jq .

# Read (replace <id>)
curl -s http://localhost:5050/Patient/<id> | jq .

# Search
curl -s 'http://localhost:5050/Patient?family=Garcia' | jq .

# Validate without persisting
curl -s -X POST 'http://localhost:5050/Patient/$validate' \
  -H 'Content-Type: application/fhir+json' \
  -d '{"resourceType":"Patient","name":[{"family":"Garcia"}]}' \
  | jq .
```

Content type: `application/fhir+json`.

## Next steps

- [Features & API](Features.md) — supported interactions
- [Configuration](Configuration.md) — auth, audit, connection strings
- [Development](Development.md) — tests, format, schema generation
- [Architecture](Architecture.md) — how the layers fit together
