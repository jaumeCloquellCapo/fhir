# Security policy

## Supported versions

FhirServer is an early open-source MVP. Security fixes are applied on a best-effort basis to the default branch.

| Version | Supported |
|---------|-----------|
| `main` (default branch) | Yes |
| Older tags / forks | Best effort |

## Reporting a vulnerability

**Do not** file a public GitHub issue for security vulnerabilities.

Please email **jaumecloquell@gmail.com** with:

- A description of the issue and its impact
- Steps to reproduce or a proof of concept if available
- Affected commit / tag if known

You should receive an acknowledgement within a few days. We will coordinate a fix and disclosure timeline with you.

## Operational guidance

This software is **not** a turnkey PHI / HIPAA / GDPR compliance product. Operators who handle real clinical data must:

- Deploy behind authentication and network controls
- Use strong database credentials and encrypted connections
- Perform their own threat modeling, backups, and compliance assessments
- Keep dependencies updated (`dotnet list package --vulnerable` when available)

Default local credentials in Compose and sample config are for **development only**.
