---
name: fhir-perf-correctness
description: >-
  FHIR F# server performance, memory, DB session, and correctness guardrails.
  Use when writing or reviewing F# code under src/FhirServer — especially
  ResourceStore, ListByType, Search, Bundle transaction, subscriptions,
  bulk dump/export/import, AppHost body reading, or auth secrets.
---

# FHIR server — perf / correctness / security

MUST BE USED for changes that touch persistence, listing, search, bundles, bulk ops, or auth bootstrap.

## Hard bans (reject in review / do not introduce)

1. **`ListByType` / `ListCurrent` + in-memory filter** (`List.filter` / `tryFind` / hydrate-all). Prefer Aidbox-style jsonb `@>` via `ListByTypeWhere` / `TryFindByTypeWhere`, or clinical `Search` (`search_*`).
2. **History / dump / export without SQL `LIMIT`** — never hydrate then `List.skip`/`truncate` for paging.
3. **`.Result` / `.Wait()` / `.GetAwaiter().GetResult()`** on the ASP.NET request / write path. Notifications and HTTP must be async or queued off-request.
4. **`RunInTransaction` that only `lock`s** without a single DB transaction when implementing FHIR `Bundle.type=transaction`.
5. **Reading entire HTTP bodies** (`ReadToEndAsync`) without Kestrel/request size limits — especially `$import` / NDJSON.
6. **Hardcoded production connection strings / passwords** in `AppHost` fallbacks outside Development.
7. **One SQL command per search-index row** inside Upsert — batch `INSERT`/`COPY`/`UNNEST`.
8. **Swallowing** `with _ -> ()` on notification / audit / domain paths — log at minimum.

## Aidbox-aligned jsonb lookups (internal, not SearchCatalog)

For top-level scalar equality (`Subscription.status`, `CodeSystem.url`, `ValueSet.url`):

```fsharp
// AND of resource @> '{"field":"value"}'::jsonb — filter before hydrate; LIMIT in SQL
store.ListByTypeWhere("Subscription", [ "status", "active" ], 100)
store.TryFindByTypeWhere("CodeSystem", [ "url", systemUrl ])
```

- Do **not** expand `GeneratedSearchCatalog` for these unless building full FHIR Search.
- Preserve tenant RLS (session already `SET LOCAL`); new WHERE must not bypass it.
- Add expression indexes for hot equality paths: `((resource->>'field'))`.
- Keep `ListByType` only for dump / export / backfill.

## Required patterns

| Concern | Do this |
|---------|---------|
| Internal field equality | `ListByTypeWhere` / `TryFindByTypeWhere` (`@>`) |
| Clinical FHIR Search | `store.Search` + `search_*` indexes |
| Dump / export | `ListByTypePage` keyset streaming — never materialize full type |
| History | SQL `ORDER BY txid DESC LIMIT/OFFSET` (+ count); no hydrate-then-skip |
| Search index writes | Batch `unnest` inserts; single multi-DELETE clear |
| Internal expansion (`_include`, chain, `_filter`) | Cap `_count` (≤500 internal / ≤1000 client) |
| `_include` | `GetMany` / `ANY(@ids)`, not N× `Get` |
| Writes + hooks | In-process sync; remote HTTP off write path |
| Create with client id | `store.Create` insert-only → `DuplicateResourceException` / Conflict |
| Bundle transaction | `RunInTransaction` + ambient session (one DB tx) |
| Process lock | No global write lock; only startup backfill gate |
| Secrets | Config/env; fail-fast if missing |
| Swagger | Development only (or authenticated) |

## Checklist before finishing a PR

- [ ] No new `ListByType` call sites except streaming/bounded admin tools
- [ ] No sync-over-async on request threads
- [ ] Pagination lives in SQL for history and list endpoints
- [ ] New list endpoints document `_count` / max page
- [ ] Tests cover: Bundle atomicity (if touched), create conflict race (if touched), oversized body (if touched)

## Related

- Broader F# review: `.cursor/skills/fsharp-reviewer/SKILL.md`
- Endpoint/OpenAPI sync: `.cursor/skills/act-swagger/SKILL.md`
