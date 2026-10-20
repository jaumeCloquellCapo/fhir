# Getting started

Run FhirServer locally and call the FHIR API in a few minutes.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://docs.docker.com/get-docker/) (Postgres via Compose)
- Optional: [jq](https://jqlang.github.io/jq/) for pretty JSON in the shell

On Homebrew macOS you may need:

```bash
export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
export PATH="/opt/homebrew/opt/dotnet/bin:$PATH"
```

## Run with PostgreSQL

1. Start the database:

   ```bash
   make db-up
   ```

   Compose starts Postgres 18 on `localhost:5432` with user/db/password `fhir` / `fhir` / `fhir` (local only—change for any shared environment).

2. Connection string: [`.env.dev`](../.env.dev) is loaded for local Compose. Template: [`.env.example`](../.env.example) — copy to a gitignored `.env` to override.

3. Run the server:

   ```bash
   dotnet run --project FhirServer
   ```

4. Open **http://localhost:5050** — try `GET /metadata` and `GET /health`.

5. Explore the API in **Swagger UI**: [http://localhost:5050/swagger](http://localhost:5050/swagger)  
   OpenAPI JSON: `http://localhost:5050/swagger/v1/swagger.json`

## Verify with curl

Auth is on by default (`fhir` / `change-me`). `/metadata` and clinical routes require credentials; `/health` and Swagger do not.

```bash
# CapabilityStatement
curl -s -u fhir:change-me http://localhost:5050/metadata | jq .

# Create a Patient
curl -s -u fhir:change-me -X POST http://localhost:5050/Patient \
  -H 'Content-Type: application/fhir+json' \
  -d '{"resourceType":"Patient","name":[{"family":"Garcia","given":["Ana"]}],"gender":"female"}' \
  | jq .

# Read (replace <id>)
curl -s -u fhir:change-me http://localhost:5050/Patient/<id> | jq .

# Search
curl -s -u fhir:change-me 'http://localhost:5050/Patient?family=Garcia' | jq .

# Validate without persisting
curl -s -u fhir:change-me -X POST 'http://localhost:5050/Patient/$validate' \
  -H 'Content-Type: application/fhir+json' \
  -d '{"resourceType":"Patient","name":[{"family":"Garcia"}]}' \
  | jq .
```

Content type: `application/fhir+json`.

## Next steps

- [Features & API](Features.md) — supported interactions
- [Configuration](Configuration.md) — auth, audit, connection strings
- [Architecture](Architecture.md) — API, database, validation, access control
- [Development](Development.md) — tests, format, schema generation
