namespace FhirServer.Domain.Ports

open FhirServer.Domain.Types

/// Outbound port: persistence of FHIR resources (envelope model).
type IResourceStore =
    abstract member Get: resourceType: string * id: string -> FhirResource option
    abstract member GetVersion: resourceType: string * id: string * vid: int64 -> HistoryEntry option
    /// Batch get by ids (single query). Prefer over N× Get for `_include`.
    abstract member GetMany: resourceType: string * ids: string list -> FhirResource list
    /// True when current row is gone but a deleted history row exists (HTTP 410).
    abstract member WasDeleted: resourceType: string * id: string -> bool
    /// Insert-only create. Raises DuplicateResourceException when id already exists.
    abstract member Create: resource: FhirResource -> FhirResource
    abstract member Upsert: resource: FhirResource -> FhirResource
    /// Soft-delete via history; returns the deleted version resource (DELETE 200 body).
    abstract member Delete: resourceType: string * id: string -> FhirResource option
    abstract member ListByType: resourceType: string -> FhirResource list

    /// Keyset page of current resources (ORDER BY id). Prefer for dump/export streaming.
    abstract member ListByTypePage:
        resourceType: string * afterId: string option * limit: int -> FhirResource list

    /// Aidbox-style jsonb containment: AND of `resource @> {"field":"value"}` for top-level string props.
    /// Filters before hydrate; default limit 100. Prefer over ListByType + in-memory filter.
    abstract member ListByTypeWhere:
        resourceType: string * filters: (string * string) list * limit: int -> FhirResource list

    /// Same WHERE as ListByTypeWhere with ORDER BY id LIMIT 1 (List.tryFind semantics).
    abstract member TryFindByTypeWhere:
        resourceType: string * filters: (string * string) list -> FhirResource option

    /// Returns (page, total matching count).
    abstract member Search: resourceType: string * query: Map<string, string list> -> FhirResource list * int
    abstract member ListHistory: filter: HistoryFilter -> HistoryEntry list * int

    /// Runs action inside one DB transaction (ambient session). Nested calls reuse the same session.
    abstract member RunInTransaction: action: (unit -> 'T) -> 'T
