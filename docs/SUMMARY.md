# Table of contents

* [Overview](../README.md)
* [Getting started](GettingStarted.md)
  * [Prerequisites](GettingStarted.md#prerequisites)
  * [Run with PostgreSQL](GettingStarted.md#run-with-postgresql)
  * [Verify with curl](GettingStarted.md#verify-with-curl)
* [Architecture](Architecture.md)
  * [Platform components](Architecture.md#platform-components)
  * [FHIR & FhirServer](Architecture.md#fhir--fhirserver)
  * [Database](Architecture.md#database)
  * [Validation pipeline](Architecture.md#validation-pipeline)
  * [Request flow](Architecture.md#request-flow-create)
  * [Code map](Architecture.md#code-map-for-contributors)
* [Features](Features.md)
  * [At a glance](Features.md#at-a-glance)

## API

* [Interactive docs (Swagger)](Features.md#interactive-docs)
* [System](Features.md#system) — `/metadata`, `/health`, `/$version`
* [CRUD, history & Bundle](Features.md#instance--type-interactions)
* [Operations](Features.md#operations) — `$validate`, `$everything`, `$export`, …
* [Search](Features.md#search-subset)
  * SearchParameters, modifiers, `_include` / `_revinclude`
  * Chaining, `_has`, composites, `_filter`
* [Bulk](Features.md#operations) — `$export` / `$import` / `$dump`
* [Terminology](Features.md#operations) — `$expand`, `$lookup`, `$validate-code`
* [Subscriptions](Features.md#subscriptions-mvp) — rest-hook + `/_hooks`

## Auth & access control

* [SMART on FHIR](Features.md#smart-on-fhir)
  * Discovery, authorize (PKCE), token, DCR
* [HTTP Basic](Configuration.md#auth-basic--smart-bearer)
* [Organization / tenant access (ORGBAC)](Features.md#organization-based-access-orgbac)
* [Audit (BALP)](Features.md#audit-optional)
* [Auth & SMART settings](Configuration.md#auth-basic--smart-bearer)

## Database

* [Envelope storage model](Architecture.md#database)
* [Connection string](Configuration.md#store-postgresql)
* [Multi-tenancy / RLS](Configuration.md#multi-tenancy-orgbac)

## FHIR configuration & validation

* [Validation (writes & `$validate`)](Features.md#validation)
* [Validation depth & profiles](Features.md#validation-depth)
* [Schema generator](../generator/README.md)
* [Schemas package](../packages/FhirServer.Schemas/README.md)
* [Validation package](../packages/FhirServer.Validation/README.md)

## Configuration & operations

* [Configuration](Configuration.md)
  * [Store](Configuration.md#store-postgresql)
  * [SMART clients](Configuration.md#smart)
  * [Subscriptions](Configuration.md#subscriptions)
  * [Audit](Configuration.md#audit)
  * [Security notes](Configuration.md#security-notes-for-operators)
* [Development](Development.md)
  * [Build & run](Development.md#build--run)
  * [Tests](Development.md#tests)
  * [CI](Development.md#ci-github-actions)
  * [Schema generation](Development.md#schema-generation)
* [Roadmap / out of scope](Features.md#out-of-scope-roadmap)

## Project

* [Contributing](../CONTRIBUTING.md)
* [Security](../SECURITY.md)
* [Support](../SUPPORT.md)
* [Code of Conduct](../CODE_OF_CONDUCT.md)
* [License](../LICENSE)
