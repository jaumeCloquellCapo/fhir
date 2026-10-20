namespace FhirServer.Domain.Services

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Schemas

module SearchEngine =

    [<RequireQualifiedAccess>]
    type StringMatch =
        | Contains
        | Exact
        | Prefix

    [<RequireQualifiedAccess>]
    type IndexKind =
        | String
        | Token
        | Ref
        | Date
        | Quantity

    [<RequireQualifiedAccess>]
    type DateComparator =
        | Eq
        | Ne
        | Gt
        | Lt
        | Ge
        | Le
        | Sa
        | Eb
        | Ap

    [<RequireQualifiedAccess>]
    type QuantityComparator =
        | Eq
        | Ne
        | Gt
        | Lt
        | Ge
        | Le
        | Ap

    [<RequireQualifiedAccess>]
    type TokenMatch =
        | Exact
        /// Best-effort hierarchy: indexed code is a prefix of the query code (ancestor).
        | Above
        /// Best-effort hierarchy: indexed code starts with the query code (descendant).
        | Below

    type StringFilter =
        { Param: string
          Match: StringMatch
          Negated: bool
          Values: string list }

    type TokenFilter =
        { Param: string
          Negated: bool
          Match: TokenMatch
          /// (system option, code) — None system matches any system.
          Values: (string option * string) list }

    type RefFilter = { Param: string; Values: string list }

    type MissingFilter =
        { Param: string
          Kind: IndexKind
          Missing: bool }

    type DateFilter =
        { Param: string
          Comparator: DateComparator
          Instant: DateTimeOffset
          /// Upper bound for `ap` (exclusive-ish window end).
          InstantHi: DateTimeOffset option }

    type QuantityFilter =
        { Param: string
          Comparator: QuantityComparator
          Value: decimal
          /// Upper bound for `ap` (±10%).
          ValueHi: decimal option
          System: string option
          Code: string option }
    [<RequireQualifiedAccess>]
    type SortField =
        | IdAsc
        | IdDesc
        | TsAsc
        | TsDesc

    type SearchPlan =
        { Ids: string list
          LastUpdatedGe: DateTimeOffset option
          LastUpdatedLe: DateTimeOffset option
          Strings: StringFilter list
          Tokens: TokenFilter list
          Refs: RefFilter list
          Missing: MissingFilter list
          Dates: DateFilter list
          Quantities: QuantityFilter list
          Sort: SortField
          Offset: int
          Count: int }
    let private str (node: JsonNode) =
        if isNull node || node.GetValueKind() = JsonValueKind.Null then
            None
        elif node.GetValueKind() = JsonValueKind.String then
            Some(node.GetValue<string>())
        else
            Some(node.ToJsonString())

    let private objectsOf (arr: JsonArray) =
        arr
        |> Seq.choose (fun (n: JsonNode) ->
            match n with
            | :? JsonObject as obj -> Some obj
            | _ -> None)
        |> Seq.toList

    let private propObjects (resource: FhirResource) (name: string) =
        match FhirJson.propArray resource name with
        | None -> []
        | Some arr -> objectsOf arr

    let private nestedObjects (node: JsonNode) (name: string) =
        match node with
        | :? JsonObject as obj ->
            match obj[name] with
            | :? JsonArray as arr -> objectsOf arr
            | _ -> []
        | _ -> []

    /// FHIR token search parameter: `system|value` or bare `value`.
    let private parseSystemValue (token: string) =
        match token.Split('|', 2) with
        | [| system; value |] -> Some system, value
        | [| value |] -> None, value
        | _ -> None, token

    let private referenceMatches (reference: string) (filter: string) =
        reference = filter
        || reference.EndsWith("/" + filter, StringComparison.Ordinal)
        || reference = "Patient/" + filter

    /// No filters → match; otherwise any filter satisfies the predicate.
    let private anyFilter (filters: string list) (predicate: string -> bool) =
        match filters with
        | [] -> true
        | xs -> List.exists predicate xs

    let private propEquals (resource: FhirResource) (field: string) (filters: string list) =
        anyFilter filters (fun filter -> FhirJson.propString resource field = Some filter)

    let private tokenMatches (items: JsonObject list) (systemField: string) (valueField: string) (filter: string) =
        let system, value = parseSystemValue filter

        items
        |> List.exists (fun item ->
            let valueOk = str item[valueField] = Some value

            let systemOk =
                match system with
                | None -> true
                | Some s -> str item[systemField] = Some s

            valueOk && systemOk)

    let private patientRefOk (resource: FhirResource) (fields: string list) (filters: string list) =
        anyFilter filters (fun filter ->
            fields
            |> List.exists (fun field ->
                match resource[field] with
                | :? JsonObject as obj ->
                    match str obj["reference"] with
                    | Some reference -> referenceMatches reference filter
                    | None -> false
                | _ -> false))

    let matchesQuery (resourceType: string) (query: Map<string, string list>) (resource: FhirResource) =
        let values key =
            query |> Map.tryFind key |> Option.defaultValue []

        let idOk = anyFilter (values "_id") (fun id -> FhirJson.id resource = id)

        if not idOk then
            false
        else
            match resourceType with
            | "Patient" ->
                let names = propObjects resource "name"

                let familyOk =
                    anyFilter (values "family") (fun filter ->
                        names
                        |> List.exists (fun n ->
                            match str n["family"] with
                            | Some family -> family.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            | None -> false))

                let givenOk =
                    anyFilter (values "given") (fun filter ->
                        names
                        |> List.exists (fun n ->
                            match n["given"] with
                            | :? JsonArray as given ->
                                given
                                |> Seq.exists (fun (g: JsonNode) ->
                                    match str g with
                                    | Some s -> s.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                    | None -> false)
                            | _ -> false))

                let nameOk =
                    anyFilter (values "name") (fun filter ->
                        names
                        |> List.exists (fun n ->
                            let family = str n["family"] |> Option.defaultValue ""

                            let given =
                                match n["given"] with
                                | :? JsonArray as arr -> arr |> Seq.choose str |> Seq.toList
                                | _ -> []

                            (family :: given)
                            |> String.concat " "
                            |> fun hay -> hay.Contains(filter, StringComparison.OrdinalIgnoreCase)))

                let genderOk = propEquals resource "gender" (values "gender")

                let birthOk =
                    anyFilter (values "birthdate") (fun filter ->
                        match FhirJson.propString resource "birthDate" with
                        | Some bd -> bd.StartsWith(filter, StringComparison.Ordinal)
                        | None -> false)

                let identifierOk =
                    let identifiers = propObjects resource "identifier"
                    anyFilter (values "identifier") (tokenMatches identifiers "system" "value")

                familyOk && givenOk && nameOk && genderOk && birthOk && identifierOk
            | "Observation" ->
                let statusOk = propEquals resource "status" (values "status")

                let subjectOk =
                    patientRefOk resource [ "subject" ] (values "patient" @ values "subject")

                let codeOk =
                    let coding = nestedObjects resource["code"] "coding"
                    anyFilter (values "code") (tokenMatches coding "system" "code")

                statusOk && subjectOk && codeOk
            | "Encounter"
            | "Condition"
            | "AllergyIntolerance"
            | "MedicationRequest"
            | "Procedure"
            | "DiagnosticReport"
            | "Immunization"
            | "CarePlan"
            | "DocumentReference" ->
                patientRefOk resource [ "subject"; "patient" ] (values "patient" @ values "subject")
            | _ -> true

    /// Parse `_include=SourceType:param` into (sourceType, json reference field names).
    let parseIncludeToken (token: string) : (string * string list) option =
        match token.Split(':', 2) with
        | [| sourceType; param |] when not (String.IsNullOrWhiteSpace sourceType) ->
            let fields =
                match sourceType, param with
                | "Patient", "organization" -> [ "managingOrganization" ]
                | _, ("patient" | "subject") -> [ "subject"; "patient" ]
                | _, p -> [ p ]

            Some(sourceType, fields)
        | _ -> None

    let private referenceTarget (node: JsonNode) : (string * string) option =
        match node with
        | :? JsonObject as obj ->
            match str obj["reference"] with
            | None -> None
            | Some reference ->
                let cleaned = reference.Trim().Split('?').[0].TrimStart('/')

                match cleaned.Split('/') with
                | [| rt; id |] when not (String.IsNullOrWhiteSpace rt || String.IsNullOrWhiteSpace id) -> Some(rt, id)
                | _ -> None
        | _ -> None

    /// Resolve `_include` resources for the given match set.
    let resolveIncludes
        (get: string * string -> FhirResource option)
        (sourceType: string)
        (includeTokens: string list)
        (matches: FhirResource list)
        : FhirResource list =
        let specs =
            includeTokens
            |> List.choose parseIncludeToken
            |> List.filter (fun (st, _) -> st.Equals(sourceType, StringComparison.Ordinal))

        if List.isEmpty specs then
            []
        else
            let seen = System.Collections.Generic.HashSet<string>()

            matches
            |> List.collect (fun resource ->
                specs
                |> List.collect (fun (_, fields) ->
                    fields
                    |> List.choose (fun field ->
                        match resource[field] with
                        | null -> None
                        | node -> referenceTarget node)
                    |> List.choose (fun (rt, id) ->
                        let key = $"{rt}/{id}"

                        if seen.Add key then get (rt, id) else None)))

    /// `_revinclude=Observation:patient` — find Observation resources pointing at matched Patients.
    let resolveRevincludes
        (search: string * Map<string, string list> -> FhirResource list)
        (matchedType: string)
        (revTokens: string list)
        (matches: FhirResource list)
        : FhirResource list =
        if List.isEmpty matches || List.isEmpty revTokens then
            []
        else
            let matchIds = matches |> List.map FhirJson.id
            let seen = System.Collections.Generic.HashSet<string>()

            revTokens
            |> List.choose (fun token ->
                match token.Split(':', 2) with
                | [| includingType; param |] when not (String.IsNullOrWhiteSpace includingType) ->
                    Some(includingType, param)
                | _ -> None)
            |> List.collect (fun (includingType, param) ->
                // Only support reverse includes that target the matched type via patient/subject/_id.
                let query =
                    match param, matchedType with
                    | ("patient" | "subject"), "Patient" -> Map.ofList [ param, matchIds ]
                    | "_id", _ -> Map.ofList [ "_id", matchIds ]
                    | _ -> Map.ofList [ param, matchIds ]

                search (includingType, query)
                |> List.filter (fun resource ->
                    let key = $"{FhirJson.resourceType resource}/{FhirJson.id resource}"
                    seen.Add key))

    let private targetTypeForRefField (refField: string) =
        match refField with
        | "patient"
        | "subject" -> "Patient"
        | "organization"
        | "managingOrganization" -> "Organization"
        | "encounter" -> "Encounter"
        | other ->
            if String.IsNullOrWhiteSpace other then
                "Patient"
            else
                string (Char.ToUpperInvariant other.[0]) + other.Substring(1)

    /// Expand `subject.name=X` / multi-hop `subject.organization.name=Y` (first hop only; nested via `search`).
    let expandChainedQuery
        (search: string * Map<string, string list> -> FhirResource list)
        (query: Map<string, string list>)
        : Map<string, string list> =
        let chainEntries, plainEntries =
            query
            |> Map.toList
            |> List.partition (fun (key, _) ->
                key.Contains('.', StringComparison.Ordinal)
                && not (key.StartsWith("_has:", StringComparison.Ordinal)))

        let plain = Map.ofList plainEntries

        chainEntries
        |> List.fold
            (fun (acc: Map<string, string list>) (key, values) ->
                match key.Split('.', 2) with
                | [| refField; nested |] ->
                    let targetType = targetTypeForRefField refField
                    // Nested may still contain dots / modifiers — `search` must expand recursively.
                    let targets = search (targetType, Map.ofList [ nested, values ])

                    let refs =
                        if List.isEmpty targets then
                            [ "__chain_nomatch__" ]
                        else
                            targets
                            |> List.collect (fun t ->
                                let id = FhirJson.id t
                                [ id; $"{targetType}/{id}" ])

                    let existing = acc |> Map.tryFind refField |> Option.defaultValue []
                    acc |> Map.add refField (existing @ refs)
                | _ -> acc)
            plain

    let private refIdsFromResource (resource: FhirResource) (param: string) =
        let fields =
            match param with
            | "patient"
            | "subject" -> [ "subject"; "patient" ]
            | "organization"
            | "managingOrganization" -> [ "managingOrganization"; "organization" ]
            | "encounter" -> [ "encounter" ]
            | other -> [ other ]

        fields
        |> List.choose (fun field ->
            match resource[field] with
            | :? JsonObject as obj ->
                match str obj["reference"] with
                | Some reference ->
                    let cleaned = reference.Trim().Split('?').[0].TrimStart('/')

                    match cleaned.Split('/') with
                    | [| _; id |] when not (String.IsNullOrWhiteSpace id) -> Some id
                    | _ when not (String.IsNullOrWhiteSpace cleaned) -> Some cleaned
                    | _ -> None
                | None -> None
            | _ -> None)

    /// Expand `_has:RevType:refParam:…` including nested `_has:…` (remainder passed to `search`).
    let expandHasQuery
        (search: string * Map<string, string list> -> FhirResource list)
        (query: Map<string, string list>)
        : Map<string, string list> =
        let hasEntries, plainEntries =
            query
            |> Map.toList
            |> List.partition (fun (key, _) -> key.StartsWith("_has:", StringComparison.Ordinal))

        let plain = Map.ofList plainEntries

        hasEntries
        |> List.fold
            (fun (acc: Map<string, string list>) (key, values) ->
                let rest = key.Substring("_has:".Length)

                // Type:refParam[:remainder] — remainder may be `_has:…` or `param` / `param:modifier`
                match rest.Split([| ':' |], 3) with
                | [| revType; refParam; nestedKey |] when
                    not (String.IsNullOrWhiteSpace revType || String.IsNullOrWhiteSpace nestedKey)
                    ->
                    let found = search (revType, Map.ofList [ nestedKey, values ])

                    let extracted =
                        found
                        |> List.collect (fun r -> refIdsFromResource r refParam)
                        |> List.distinct

                    let ids =
                        if List.isEmpty found || List.isEmpty extracted then
                            [ "__has_nomatch__" ]
                        else
                            extracted

                    let existing = acc |> Map.tryFind "_id" |> Option.defaultValue []
                    acc |> Map.add "_id" (existing @ ids)
                | [| revType; refParam |] when not (String.IsNullOrWhiteSpace revType) ->
                    let found = search (revType, Map.ofList [ refParam, values ])

                    let extracted =
                        found
                        |> List.collect (fun r -> refIdsFromResource r refParam)
                        |> List.distinct

                    let ids =
                        if List.isEmpty found || List.isEmpty extracted then
                            [ "__has_nomatch__" ]
                        else
                            extracted

                    let existing = acc |> Map.tryFind "_id" |> Option.defaultValue []
                    acc |> Map.add "_id" (existing @ ids)
                | _ -> acc)
            plain

    let private firstQuery (query: Map<string, string list>) key =
        query |> Map.tryFind key |> Option.bind List.tryHead

    let private valuesOf (query: Map<string, string list>) key =
        query |> Map.tryFind key |> Option.defaultValue []

    let private parsePaging (query: Map<string, string list>) =
        let offset =
            firstQuery query "_offset"
            |> Option.orElse (firstQuery query "_getpagesoffset")
            |> Option.bind (fun s ->
                match Int32.TryParse s with
                | true, n when n >= 0 -> Some n
                | _ -> None)
            |> Option.defaultValue 0

        let count =
            firstQuery query "_count"
            |> Option.bind (fun s ->
                match Int32.TryParse s with
                // Allow up to 10k so internal chain/_has expansion is not silently truncated at 200.
                | true, n when n > 0 -> Some(min n 10_000)
                | _ -> None)
            |> Option.defaultValue 50

        offset, count

    let private parseSort (query: Map<string, string list>) =
        match firstQuery query "_sort" with
        | Some "-_lastUpdated" -> SortField.TsDesc
        | Some "_lastUpdated" -> SortField.TsAsc
        | Some "-_id" -> SortField.IdDesc
        | _ -> SortField.IdAsc

    let private parseInstant (raw: string) =
        let toUtc (dto: DateTimeOffset) = dto.ToUniversalTime()

        match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
        | true, dto -> Some(toUtc dto)
        | _ ->
            match
                DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                )
            with
            | true, dto -> Some(toUtc dto)
            | _ -> None

    /// Parse FHIR date search prefixes on `_lastUpdated` (ge/gt/le/lt/eq or bare → ge).
    let private parseLastUpdated (values: string list) =
        let mutable ge: DateTimeOffset option = None
        let mutable le: DateTimeOffset option = None

        for raw in values do
            let prefix, rest =
                if raw.Length >= 2 && (raw.StartsWith("ge", StringComparison.Ordinal) || raw.StartsWith("gt", StringComparison.Ordinal) || raw.StartsWith("le", StringComparison.Ordinal) || raw.StartsWith("lt", StringComparison.Ordinal) || raw.StartsWith("eq", StringComparison.Ordinal)) then
                    raw.Substring(0, 2), raw.Substring(2)
                else
                    "ge", raw

            match parseInstant rest with
            | None -> ()
            | Some dto ->
                match prefix with
                | "ge"
                | "gt" ->
                    ge <-
                        match ge with
                        | Some existing when existing >= dto -> Some existing
                        | _ -> Some dto
                | "le"
                | "lt" ->
                    le <-
                        match le with
                        | Some existing when existing <= dto -> Some existing
                        | _ -> Some dto
                | "eq" ->
                    ge <- Some dto
                    le <- Some(dto.AddSeconds 1.0)
                | _ -> ge <- Some dto

        ge, le

    let private parseTokenValues (filters: string list) =
        filters
        |> List.map (fun token ->
            match token.Split('|', 2) with
            | [| system; value |] when system <> "" -> Some system, value
            | [| value |] -> None, value
            | _ -> None, token)

    let private parseParamKey (key: string) =
        // Ignore control params and _has / chained keys here.
        if key.StartsWith('_') || key.Contains('.', StringComparison.Ordinal) then
            key, None
        else
            match key.Split(':', 2) with
            | [| name; modifier |] when name <> "" && modifier <> "" ->
                name, Some(modifier.ToLowerInvariant())
            | [| name |] -> name, None
            | _ -> key, None

    let private entriesFor (query: Map<string, string list>) (baseName: string) =
        query
        |> Map.toList
        |> List.choose (fun (key, values) ->
            let name, modifier = parseParamKey key

            if name.Equals(baseName, StringComparison.Ordinal) then
                Some(modifier, values)
            else
                None)

    let private parseMissingFlag (values: string list) =
        match values |> List.tryHead |> Option.map (fun s -> s.Trim().ToLowerInvariant()) with
        | Some "true"
        | Some "1"
        | Some "yes" -> Some true
        | Some "false"
        | Some "0"
        | Some "no" -> Some false
        | _ -> Some true

    let private parseDatePrefix (raw: string) : (DateComparator * string) option =
        let prefixes = [ "eq"; "ne"; "gt"; "lt"; "ge"; "le"; "sa"; "eb"; "ap" ]

        let prefix, rest =
            match prefixes |> List.tryFind (fun p -> raw.StartsWith(p, StringComparison.Ordinal)) with
            | Some p when raw.Length > p.Length -> p, raw.Substring(p.Length)
            | _ -> "eq", raw

        let cmp =
            match prefix with
            | "ne" -> DateComparator.Ne
            | "gt" -> DateComparator.Gt
            | "lt" -> DateComparator.Lt
            | "ge" -> DateComparator.Ge
            | "le" -> DateComparator.Le
            | "sa" -> DateComparator.Sa
            | "eb" -> DateComparator.Eb
            | "ap" -> DateComparator.Ap
            | _ -> DateComparator.Eq

        Some(cmp, rest)

    let private approximateDateWindow (instant: DateTimeOffset) =
        let now = DateTimeOffset.UtcNow
        let span = now - instant
        let tenth = TimeSpan.FromTicks(int64 (float (abs span.Ticks) * 0.1))
        // Floor: at least ±12h so day-precision values still match nearby days.
        let delta =
            if tenth < TimeSpan.FromHours 12.0 then
                TimeSpan.FromHours 12.0
            else
                tenth

        instant - delta, instant + delta

    let private parseQuantityValue (raw: string) : (QuantityComparator * decimal * string option * string option) option =
        let prefix, rest =
            if
                raw.Length >= 2
                && (raw.StartsWith("ge", StringComparison.Ordinal)
                    || raw.StartsWith("gt", StringComparison.Ordinal)
                    || raw.StartsWith("le", StringComparison.Ordinal)
                    || raw.StartsWith("lt", StringComparison.Ordinal)
                    || raw.StartsWith("eq", StringComparison.Ordinal)
                    || raw.StartsWith("ne", StringComparison.Ordinal)
                    || raw.StartsWith("ap", StringComparison.Ordinal))
            then
                raw.Substring(0, 2), raw.Substring(2)
            else
                "eq", raw

        let cmp =
            match prefix with
            | "ne" -> QuantityComparator.Ne
            | "gt" -> QuantityComparator.Gt
            | "lt" -> QuantityComparator.Lt
            | "ge" -> QuantityComparator.Ge
            | "le" -> QuantityComparator.Le
            | "ap" -> QuantityComparator.Ap
            | _ -> QuantityComparator.Eq

        let parts = rest.Split('|')

        let numberPart = parts.[0]

        match Decimal.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, value ->
            let system = if parts.Length > 1 && parts.[1] <> "" then Some parts.[1] else None
            let code = if parts.Length > 2 && parts.[2] <> "" then Some parts.[2] else None
            Some(cmp, value, system, code)
        | _ -> None

    let private catalogParams (resourceType: string) =
        GeneratedSearchCatalog.forResource resourceType

    let private toIndexKind =
        function
        | GeneratedSearchCatalog.IndexKind.String -> IndexKind.String
        | GeneratedSearchCatalog.IndexKind.Token -> IndexKind.Token
        | GeneratedSearchCatalog.IndexKind.Ref -> IndexKind.Ref
        | GeneratedSearchCatalog.IndexKind.Date -> IndexKind.Date
        | GeneratedSearchCatalog.IndexKind.Quantity -> IndexKind.Quantity
        | GeneratedSearchCatalog.IndexKind.Composite -> IndexKind.String // unused for filters

    let private toStringMatch =
        function
        | GeneratedSearchCatalog.StringMatch.Contains -> StringMatch.Contains
        | GeneratedSearchCatalog.StringMatch.Exact -> StringMatch.Exact

    let private stringParamSpecs (resourceType: string) =
        catalogParams resourceType
        |> List.choose (fun p ->
            match p.Kind with
            | GeneratedSearchCatalog.IndexKind.String ->
                Some(p.Code, p.IndexParam, toStringMatch p.StringMatch, IndexKind.String)
            | _ -> None)

    let private tokenParamSpecs (resourceType: string) =
        catalogParams resourceType
        |> List.choose (fun p ->
            match p.Kind with
            | GeneratedSearchCatalog.IndexKind.Token -> Some(p.Code, p.IndexParam, IndexKind.Token)
            | _ -> None)

    let private refParamSpecs (resourceType: string) =
        catalogParams resourceType
        |> List.choose (fun p ->
            match p.Kind with
            | GeneratedSearchCatalog.IndexKind.Ref -> Some(p.Code, p.IndexParam)
            | _ -> None)

    let private dateParamSpecs (resourceType: string) =
        catalogParams resourceType
        |> List.choose (fun p ->
            match p.Kind with
            | GeneratedSearchCatalog.IndexKind.Date -> Some(p.Code, p.IndexParam)
            | _ -> None)

    let private quantityParamSpecs (resourceType: string) =
        catalogParams resourceType
        |> List.choose (fun p ->
            match p.Kind with
            | GeneratedSearchCatalog.IndexKind.Quantity -> Some(p.Code, p.IndexParam)
            | _ -> None)

    let private compositeParamSpecs (resourceType: string) =
        catalogParams resourceType
        |> List.choose (fun p ->
            match p.Kind, p.Extractor with
            | GeneratedSearchCatalog.IndexKind.Composite, GeneratedSearchCatalog.Extractor.Composite components ->
                Some(p.Code, components)
            | _ -> None)

    let private stringFilters (resourceType: string) (query: Map<string, string list>) =
        stringParamSpecs resourceType
        |> List.collect (fun (code, indexParam, defaultMatch, _) ->
            entriesFor query code
            |> List.choose (fun (modifier, values) ->
                let lowered = values |> List.map (fun (s: string) -> s.ToLowerInvariant())

                let result: StringFilter option =
                    match modifier with
                    | Some "missing" -> None
                    | Some "exact" ->
                        Some {
                            Param = indexParam
                            Match = StringMatch.Exact
                            Negated = false
                            Values = lowered
                        }
                    | Some "contains" ->
                        Some {
                            Param = indexParam
                            Match = StringMatch.Contains
                            Negated = false
                            Values = lowered
                        }
                    | Some "not" ->
                        Some {
                            Param = indexParam
                            Match = StringMatch.Exact
                            Negated = true
                            Values = lowered
                        }
                    | Some _ -> None
                    | None ->
                        Some {
                            Param = indexParam
                            Match = defaultMatch
                            Negated = false
                            Values = lowered
                        }

                result))

    let private tokenFilters (resourceType: string) (query: Map<string, string list>) : TokenFilter list =
        tokenParamSpecs resourceType
        |> List.collect (fun (code, indexParam, _) ->
            entriesFor query code
            |> List.choose (fun (modifier, values) ->
                match modifier with
                | Some "missing" -> None
                | Some "not" ->
                    Some {
                        Param = indexParam
                        Negated = true
                        Match = TokenMatch.Exact
                        Values = parseTokenValues values
                    }
                | Some "above" ->
                    Some {
                        Param = indexParam
                        Negated = false
                        Match = TokenMatch.Above
                        Values = parseTokenValues values
                    }
                | Some "below" ->
                    Some {
                        Param = indexParam
                        Negated = false
                        Match = TokenMatch.Below
                        Values = parseTokenValues values
                    }
                | Some _ -> None
                | None ->
                    Some {
                        Param = indexParam
                        Negated = false
                        Match = TokenMatch.Exact
                        Values = parseTokenValues values
                    }))
    let private refFilters (resourceType: string) (query: Map<string, string list>) : RefFilter list =
        refParamSpecs resourceType
        |> List.collect (fun (queryParam, indexParam) ->
            entriesFor query queryParam
            |> List.choose (fun (modifier, values) ->
                match modifier with
                | Some "missing" -> None
                | Some _ -> None
                | None when values = [] -> None
                | None -> Some { Param = indexParam; Values = values }))
        |> List.groupBy (fun f -> f.Param)
        |> List.map (fun (param, filters) -> {
            Param = param
            Values = filters |> List.collect (fun f -> f.Values)
        })

    let private missingFilters (resourceType: string) (query: Map<string, string list>) : MissingFilter list =
        catalogParams resourceType
        |> List.filter (fun p ->
            match p.Kind with
            | GeneratedSearchCatalog.IndexKind.Composite -> false
            | _ -> true)
        |> List.collect (fun p ->
            entriesFor query p.Code
            |> List.choose (fun (modifier, values) ->
                match modifier with
                | Some "missing" ->
                    parseMissingFlag values
                    |> Option.map (fun missing -> {
                        Param = p.IndexParam
                        Kind = toIndexKind p.Kind
                        Missing = missing
                    })
                | _ -> None))

    /// Expand `code-value-quantity=loinc|x$gt100|u` into component query entries (AND).
    let private expandCompositeQuery (resourceType: string) (query: Map<string, string list>) =
        compositeParamSpecs resourceType
        |> List.fold
            (fun (acc: Map<string, string list>) (code, components) ->
                match acc |> Map.tryFind code with
                | None -> acc
                | Some values ->
                    let without = acc |> Map.remove code

                    values
                    |> List.fold
                        (fun m raw ->
                            let parts = raw.Split('$')

                            if parts.Length <> components.Length then
                                m
                            else
                                List.zip components (Array.toList parts)
                                |> List.fold
                                    (fun m' ((compCode, _), part) ->
                                        let existing = m' |> Map.tryFind compCode |> Option.defaultValue []
                                        m' |> Map.add compCode (existing @ [ part ]))
                                    m)
                        without)
            query

    let private toDateFilter (param: string) (cmp: DateComparator) (dto: DateTimeOffset) =
        match cmp with
        | DateComparator.Ap ->
            let lo, hi = approximateDateWindow dto

            {
                Param = param
                Comparator = DateComparator.Ap
                Instant = lo
                InstantHi = Some hi
            }
        | _ ->
            {
                Param = param
                Comparator = cmp
                Instant = dto
                InstantHi = None
            }

    let private dateFilters (resourceType: string) (query: Map<string, string list>) : DateFilter list =
        dateParamSpecs resourceType
        |> List.collect (fun (code, indexParam) ->
            entriesFor query code
            |> List.collect (fun (modifier, values) ->
                match modifier with
                | Some "missing" -> []
                | Some _ -> []
                | None ->
                    values
                    |> List.choose (fun raw ->
                        match parseDatePrefix raw with
                        | None -> None
                        | Some(cmp, rest) ->
                            match parseInstant rest with
                            | Some dto -> Some(toDateFilter indexParam cmp dto)
                            | None ->
                                match
                                    DateTimeOffset.TryParse(
                                        rest,
                                        CultureInfo.InvariantCulture,
                                        DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                                    )
                                with
                                | true, dto -> Some(toDateFilter indexParam cmp (dto.ToUniversalTime()))
                                | _ -> None)))

    let private quantityFilters (resourceType: string) (query: Map<string, string list>) : QuantityFilter list =
        quantityParamSpecs resourceType
        |> List.collect (fun (code, indexParam) ->
            entriesFor query code
            |> List.collect (fun (modifier, values) ->
                match modifier with
                | Some "missing" -> []
                | Some _ -> []
                | None ->
                    values
                    |> List.choose (fun raw ->
                        match parseQuantityValue raw with
                        | None -> None
                        | Some(cmp, value, system, codeVal) ->
                            let valueHi =
                                match cmp with
                                | QuantityComparator.Ap ->
                                    let margin = abs value * 0.1m
                                    Some(value + margin)
                                | _ -> None

                            let valueLo =
                                match cmp with
                                | QuantityComparator.Ap ->
                                    let margin = abs value * 0.1m
                                    value - margin
                                | _ -> value

                            Some {
                                Param = indexParam
                                Comparator = cmp
                                Value = valueLo
                                ValueHi = valueHi
                                System = system
                                Code = codeVal
                            })))    /// Build a search plan from a FHIR query map (indexed params only; others ignored).
    let toSearchPlan (resourceType: string) (query: Map<string, string list>) : SearchPlan =
        let query = expandCompositeQuery resourceType query
        let offset, count = parsePaging query
        let ge, le = parseLastUpdated (valuesOf query "_lastUpdated")

        { Ids = valuesOf query "_id"
          LastUpdatedGe = ge
          LastUpdatedLe = le
          Strings = stringFilters resourceType query
          Tokens = tokenFilters resourceType query
          Refs = refFilters resourceType query
          Missing = missingFilters resourceType query
          Dates = dateFilters resourceType query
          Quantities = quantityFilters resourceType query
          Sort = parseSort query
          Offset = offset
          Count = count }
    let applyPagingSort (query: Map<string, string list>) (items: FhirResource list) =
        let sorted =
            match parseSort query with
            | SortField.TsDesc ->
                items
                |> List.sortByDescending (fun r ->
                    FhirJson.lastUpdated r |> Option.defaultValue DateTimeOffset.MinValue)
            | SortField.TsAsc ->
                items
                |> List.sortBy (fun r -> FhirJson.lastUpdated r |> Option.defaultValue DateTimeOffset.MinValue)
            | SortField.IdDesc -> items |> List.sortByDescending FhirJson.id
            | SortField.IdAsc -> items |> List.sortBy FhirJson.id

        let offset, count = parsePaging query
        sorted |> List.skip (min offset sorted.Length) |> List.truncate count, sorted.Length
