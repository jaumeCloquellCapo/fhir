namespace FhirServer.Domain.Services

open System
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types

module SearchEngine =

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

    let applyPagingSort (query: Map<string, string list>) (items: FhirResource list) =
        let first key =
            query |> Map.tryFind key |> Option.bind List.tryHead

        let sorted =
            match first "_sort" with
            | Some "-_lastUpdated" ->
                items
                |> List.sortByDescending (fun r ->
                    FhirJson.lastUpdated r |> Option.defaultValue DateTimeOffset.MinValue)
            | Some "_lastUpdated" ->
                items
                |> List.sortBy (fun r -> FhirJson.lastUpdated r |> Option.defaultValue DateTimeOffset.MinValue)
            | Some "-_id" -> items |> List.sortByDescending FhirJson.id
            | _ -> items |> List.sortBy FhirJson.id

        let offset =
            first "_offset"
            |> Option.orElse (first "_getpagesoffset")
            |> Option.bind (fun s ->
                match Int32.TryParse s with
                | true, n when n >= 0 -> Some n
                | _ -> None)
            |> Option.defaultValue 0

        let count =
            first "_count"
            |> Option.bind (fun s ->
                match Int32.TryParse s with
                | true, n when n > 0 -> Some(min n 200)
                | _ -> None)
            |> Option.defaultValue 50

        sorted |> List.skip (min offset sorted.Length) |> List.truncate count, sorted.Length
