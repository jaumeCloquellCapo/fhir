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

In-process E2E tests live under `tests/FhirServer.E2E/` (one file per endpoint area).

```bash
make test
# or: dotnet test tests/FhirServer.E2E
```

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

- Prefer changes in **Domain** / **Application** before touching HTTP adapters
- New SQL behavior: implement once in `EnvelopeResourceStore` or the dialect interface—avoid duplicating store logic
- New FHIR interactions: add a handler, route in `AppHost`, and an E2E test under `Endpoints/`

## Pull requests

See [CONTRIBUTING.md](../CONTRIBUTING.md). Keep PRs focused; include tests for behavior changes when practical.
