# Development

How to build, test, format, and regenerate schemas.

## Build & run

```bash
dotnet tool restore   # Fantomas, FSharpLint
make restore
make build
make run              # or: make watch
```

## Tests

In-process E2E tests live under `tests/FhirServer.E2E/` (one file per endpoint area). Each factory creates and drops an isolated Postgres database. Start Postgres first:

```bash
make db-up
make test
# or: dotnet test tests/FhirServer.E2E
```

Optional override: `FHIR_E2E_ADMIN_CONNECTION` (defaults to local Compose credentials).

## CI (GitHub Actions)

Workflows under [`.github/workflows/`](../.github/workflows/):

| Workflow | What it runs |
|----------|----------------|
| [api.yml](../.github/workflows/api.yml) | Build `FhirServer` + E2E tests |
| [generator.yml](../.github/workflows/generator.yml) | Build `FhirFsGen`, smoke-generate `Patient`, build schema packages |

Both run on `push` / `pull_request` to `main` (path-filtered) and support `workflow_dispatch`.

## Format & lint

```bash
make format
make format-check
make lint
```

Local tools are declared in [`.config/dotnet-tools.json`](../.config/dotnet-tools.json).

## Schema generation

Schemas and the validator are generated from the official HL7 R5 package. Details: [generator/README.md](../generator/README.md).

```bash
make generate          # Patient (+ sync into packages/)
make generate-r5-all   # all resources + sync
make sync-schemas      # copy generator/out/r5 → packages/ without regenerating
```

Commit regenerated `packages/` artifacts when the generator or package version changes so consumers can build without running the generator.

## Project layout tips

- New FHIR behavior: resource handler + route in `AppHost` + E2E test under `Endpoints/`
- Persistence: extend the envelope store / SQL session once—avoid duplicating write or search logic per endpoint
- Keep `/metadata`, Swagger, and [Features.md](Features.md) aligned when the HTTP surface changes (see `.cursor/skills/act-swagger`)

Code map: [Architecture.md](Architecture.md)#code-map-for-contributors.

## Pull requests

See [CONTRIBUTING.md](../CONTRIBUTING.md). Keep PRs focused; include tests for behavior changes when practical.
