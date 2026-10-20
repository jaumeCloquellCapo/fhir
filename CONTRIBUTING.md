# Contributing to FhirServer

Thanks for your interest in contributing. This project is early (MVP); clear, small contributions help most.

## Ways to contribute

- Report bugs and propose features via GitHub Issues
- Improve documentation under `docs/` and the root `README.md`
- Fix bugs or add tests in `tests/FhirServer.E2E`
- Implement roadmap items listed in [docs/Features.md](docs/Features.md) (out of scope section)

## Development setup

Follow [docs/GettingStarted.md](docs/GettingStarted.md) and [docs/Development.md](docs/Development.md).

Before opening a PR:

```bash
make format-check
make lint
make test
```

GitHub Actions runs the API E2E suite and a generator smoke build on PRs that touch the relevant paths (see [docs/Development.md](docs/Development.md)).

## Pull request guidelines

1. **One concern per PR** — easier to review and revert
2. **Describe why** — link issues when applicable
3. **Add or update tests** for behavior changes when practical
4. **Do not commit secrets** — use user secrets or env vars for credentials
5. **Match existing style** — Fantomas formatting; F# idioms (modules, explicit ports)

## Code of conduct

Participation is governed by our [Code of Conduct](CODE_OF_CONDUCT.md).

## Security

Please report vulnerabilities privately — see [SECURITY.md](SECURITY.md). Do not open public issues for undisclosed security bugs.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
