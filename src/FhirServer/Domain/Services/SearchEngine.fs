namespace FhirServer.Domain.Services

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types

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

    [<RequireQualifiedAccess>]
    type QuantityComparator =
        | Eq
        | Ne
        | Gt
        | Lt
        | Ge
        | Le

    type StringFilter =
        { Param: string
          Match: StringMatch
          Values: string list }

    type TokenFilter =
        { Param: string
          Negated: bool
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
          Instant: DateTimeOffset }

    type QuantityFilter =
        { Param: string
          Comparator: QuantityComparator
          Value: decimal
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

    /// Expand `subject.name=X` / `patient.family=Y` into concrete reference filters.
    let expandChainedQuery
        (search: string * Map<string, string list> -> FhirResource list)
        (query: Map<string, string list>)
        : Map<string, string list> =
        let chainEntries, plainEntries =
            query
            |> Map.toList
            |> List.partition (fun (key, _) -> key.Contains('.', StringComparison.Ordinal))

        let plain = Map.ofList plainEntries

        chainEntries
        |> List.fold
            (fun (acc: Map<string, string list>) (key, values) ->
                match key.Split('.', 2) with
                | [| refField; nested |] ->
                    let targetType =
                        match refField with
                        | "patient"
                        | "subject" -> "Patient"
                        | "organization"
                        | "managingOrganization" -> "Organization"
                        | "encounter" -> "Encounter"
                        | other ->
                            // Best-effort: capitalize as resource type name.
                            if String.IsNullOrWhiteSpace other then
                                "Patient"
                            else
                                string (Char.ToUpperInvariant other.[0]) + other.Substring(1)

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

    /// Expand `_has:RevType:refParam:nestedParam=value` into `_id` filters on the source type.
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

                match rest.Split(':', 3) with
                | [| revType; refParam; nestedParam |] when
                    not (String.IsNullOrWhiteSpace revType || String.IsNullOrWhiteSpace nestedParam)
                    ->
                    let found = search (revType, Map.ofList [ nestedParam, values ])

                    let ids =
                        if List.isEmpty found then
                            [ "__has_nomatch__" ]
                        else
                            found
                            |> List.collect (fun r -> refIdsFromResource r refParam)
                            |> List.distinct

                    let existing = acc |> Map.tryFind "_id" |> Option.defaultValue []
                    acc |> Map.add "_id" (existing @ ids)
                | [| revType; refParam |] when not (String.IsNullOrWhiteSpace revType) ->
                    // `_has:Observation:patient=Patient/x` → Observations pointing at that patient → inject ids
                    let found = search (revType, Map.ofList [ refParam, values ])

                    let ids =
                        if List.isEmpty found then
                            [ "__has_nomatch__" ]
                        else
                            found
                            |> List.collect (fun r -> refIdsFromResource r refParam)
                            |> List.distinct

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
                | true, n when n > 0 -> Some(min n 200)
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
        match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
        | true, dto -> Some dto
        | _ ->
            match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
            | true, dto -> Some dto
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
        let prefixes = [ "eq"; "ne"; "gt"; "lt"; "ge"; "le"; "sa"; "eb" ]

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
            | _ -> DateComparator.Eq

        Some(cmp, rest)

    let private parseQuantityValue (raw: string) : (QuantityComparator * decimal * string option * string option) option =
        let prefix, rest =
            if
                raw.Length >= 2
                && (raw.StartsWith("ge", StringComparison.Ordinal)
                    || raw.StartsWith("gt", StringComparison.Ordinal)
                    || raw.StartsWith("le", StringComparison.Ordinal)
                    || raw.StartsWith("lt", StringComparison.Ordinal)
                    || raw.StartsWith("eq", StringComparison.Ordinal)
                    || raw.StartsWith("ne", StringComparison.Ordinal))
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
            | _ -> QuantityComparator.Eq

        let parts = rest.Split('|')

        let numberPart = parts.[0]

        match Decimal.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, value ->
            let system = if parts.Length > 1 && parts.[1] <> "" then Some parts.[1] else None
            let code = if parts.Length > 2 && parts.[2] <> "" then Some parts.[2] else None
            Some(cmp, value, system, code)
        | _ -> None

    let private stringParamSpecs (resourceType: string) =
        match resourceType with
        | "Patient" ->
            [
                "family", StringMatch.Contains, IndexKind.String
                "given", StringMatch.Contains, IndexKind.String
                "name", StringMatch.Contains, IndexKind.String
                "gender", StringMatch.Exact, IndexKind.String
            ]
        | "Observation" -> [ "status", StringMatch.Exact, IndexKind.String ]
        | _ -> []

    let private tokenParamSpecs (resourceType: string) =
        match resourceType with
        | "Patient" -> [ "identifier", IndexKind.Token ]
        | "Observation" -> [ "code", IndexKind.Token ]
        | _ -> []

    let private refParamSpecs (resourceType: string) =
        match resourceType with
        | "Observation" -> [ "patient", "subject"; "subject", "subject" ]
        | "Encounter"
        | "Condition"
        | "AllergyIntolerance"
        | "MedicationRequest"
        | "Procedure"
        | "DiagnosticReport"
        | "Immunization"
        | "CarePlan"
        | "DocumentReference" -> [ "patient", "patient"; "subject", "patient" ]
        | _ -> []

    let private dateParamSpecs (resourceType: string) =
        match resourceType with
        | "Patient" -> [ "birthdate" ]
        | "Observation" -> [ "date" ]
        | _ -> []

    let private quantityParamSpecs (resourceType: string) =
        match resourceType with
        | "Observation" -> [ "value-quantity" ]
        | _ -> []

    let private stringFilters (resourceType: string) (query: Map<string, string list>) =
        stringParamSpecs resourceType
        |> List.collect (fun (param, defaultMatch, _) ->
            entriesFor query param
            |> List.choose (fun (modifier, values) ->
                match modifier with
                | Some "missing" -> None
                | Some "exact" ->
                    Some {
                        Param = param
                        Match = StringMatch.Exact
                        Values = values |> List.map (fun s -> s.ToLowerInvariant())
                    }
                | Some "contains" ->
                    Some {
                        Param = param
                        Match = StringMatch.Contains
                        Values = values |> List.map (fun s -> s.ToLowerInvariant())
                    }
                | Some _ -> None
                | None ->
                    Some {
                        Param = param
                        Match = defaultMatch
                        Values = values |> List.map (fun s -> s.ToLowerInvariant())
                    }))

    let private tokenFilters (resourceType: string) (query: Map<string, string list>) : TokenFilter list =
        tokenParamSpecs resourceType
        |> List.collect (fun (param, _) ->
            entriesFor query param
            |> List.choose (fun (modifier, values) ->
                match modifier with
                | Some "missing" -> None
                | Some "not" ->
                    Some {
                        Param = param
                        Negated = true
                        Values = parseTokenValues values
                    }
                | Some _ -> None
                | None ->
                    Some {
                        Param = param
                        Negated = false
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
        let specs =
            [
                for param, _, kind in stringParamSpecs resourceType do
                    yield param, kind
                for param, kind in tokenParamSpecs resourceType do
                    yield param, kind
                for queryParam, indexParam in refParamSpecs resourceType do
                    yield queryParam, IndexKind.Ref
                    yield indexParam, IndexKind.Ref
                for param in dateParamSpecs resourceType do
                    yield param, IndexKind.Date
                for param in quantityParamSpecs resourceType do
                    yield param, IndexKind.Quantity
            ]
            |> List.distinctBy fst

        specs
        |> List.collect (fun (param, kind) ->
            entriesFor query param
            |> List.choose (fun (modifier, values) ->
                match modifier with
                | Some "missing" ->
                    parseMissingFlag values
                    |> Option.map (fun missing -> {
                        Param =
                            match kind, param with
                            | IndexKind.Ref, "patient" when resourceType = "Observation" -> "subject"
                            | IndexKind.Ref, "subject" when resourceType <> "Observation" -> "patient"
                            | _ -> param
                        Kind = kind
                        Missing = missing
                    })
                | _ -> None))

    let private dateFilters (resourceType: string) (query: Map<string, string list>) : DateFilter list =
        dateParamSpecs resourceType
        |> List.collect (fun param ->
            entriesFor query param
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
                            | Some dto ->
                                Some {
                                    Param = param
                                    Comparator = cmp
                                    Instant = dto
                                }
                            | None ->
                                // Allow bare dates like 1990-01-01
                                match
                                    DateTimeOffset.TryParse(
                                        rest,
                                        CultureInfo.InvariantCulture,
                                        DateTimeStyles.AssumeUniversal
                                    )
                                with
                                | true, dto ->
                                    Some {
                                        Param = param
                                        Comparator = cmp
                                        Instant = dto
                                    }
                                | _ -> None)))

    let private quantityFilters (resourceType: string) (query: Map<string, string list>) : QuantityFilter list =
        quantityParamSpecs resourceType
        |> List.collect (fun param ->
            entriesFor query param
            |> List.collect (fun (modifier, values) ->
                match modifier with
                | Some "missing" -> []
                | Some _ -> []
                | None ->
                    values
                    |> List.choose (fun raw ->
                        match parseQuantityValue raw with
                        | None -> None
                        | Some(cmp, value, system, code) ->
                            Some {
                                Param = param
                                Comparator = cmp
                                Value = value
                                System = system
                                Code = code
                            })))

    /// Build a search plan from a FHIR query map (indexed params only; others ignored).
    let toSearchPlan (resourceType: string) (query: Map<string, string list>) : SearchPlan =
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
