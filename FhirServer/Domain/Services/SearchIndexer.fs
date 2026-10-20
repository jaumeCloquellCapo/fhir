namespace FhirServer.Domain.Services

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Schemas

/// Extract FHIR search index rows from a resource using GeneratedSearchCatalog extractors.
module SearchIndexer =

    type StringRow = { Param: string; Value: string }

    type TokenRow =
        { Param: string
          System: string
          Code: string }

    type RefRow = { Param: string; Target: string }

    type DateRow =
        { Param: string
          StartTs: DateTimeOffset
          EndTs: DateTimeOffset }

    type QuantityRow =
        { Param: string
          Value: decimal
          System: string
          Code: string }

    type IndexRows =
        { Strings: StringRow list
          Tokens: TokenRow list
          Refs: RefRow list
          Dates: DateRow list
          Quantities: QuantityRow list }

    let empty: IndexRows =
        { Strings = []
          Tokens = []
          Refs = []
          Dates = []
          Quantities = [] }

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

    let private norm (s: string) = s.Trim().ToLowerInvariant()

    let private addString (param: string) (value: string) (acc: StringRow list) =
        if String.IsNullOrWhiteSpace value then
            acc
        else
            { Param = param; Value = norm value } :: acc

    let private addToken (param: string) (system: string option) (code: string) (acc: TokenRow list) =
        if String.IsNullOrWhiteSpace code then
            acc
        else
            { Param = param
              System = system |> Option.defaultValue "" |> fun s -> s.Trim()
              Code = code.Trim() }
            :: acc

    let private refTargets (reference: string) =
        let cleaned = reference.Trim().Split('?').[0].TrimStart('/')

        match cleaned.Split('/') with
        | [| _; id |] when not (String.IsNullOrWhiteSpace id) -> [ cleaned; id ]
        | _ -> [ cleaned ]
        |> List.distinct

    let private addRef (param: string) (reference: string) (acc: RefRow list) =
        refTargets reference
        |> List.fold (fun rows target -> { Param = param; Target = target } :: rows) acc

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

    let private addDate (param: string) (startTs: DateTimeOffset) (endTs: DateTimeOffset) (acc: DateRow list) =
        { Param = param
          StartTs = startTs
          EndTs = endTs }
        :: acc

    let private addDateFromString (param: string) (raw: string) (acc: DateRow list) =
        match parseInstant raw with
        | None -> acc
        | Some startTs ->
            let endTs =
                if raw.Length <= 10 then
                    startTs.AddDays 1.0
                else
                    startTs.AddSeconds 1.0

            addDate param startTs endTs acc

    let private addQuantity
        (param: string)
        (value: decimal)
        (system: string option)
        (code: string option)
        (acc: QuantityRow list)
        =
        { Param = param
          Value = value
          System = system |> Option.defaultValue ""
          Code = code |> Option.defaultValue "" }
        :: acc

    let private navigate (root: JsonNode) (path: string list) : JsonNode list =
        let rec go (nodes: JsonNode list) (remaining: string list) =
            match remaining with
            | [] -> nodes
            | name :: rest ->
                nodes
                |> List.collect (fun node ->
                    match node with
                    | :? JsonObject as obj ->
                        match obj[name] with
                        | null -> []
                        | :? JsonArray as arr -> arr |> Seq.choose (fun n -> if isNull n then None else Some n) |> Seq.toList
                        | child -> [ child ]
                    | _ -> [])
                |> fun next -> go next rest

        go [ root ] path

    let private observationEffectiveDates (param: string) (resource: FhirResource) =
        let mutable dates: DateRow list = []

        match FhirJson.propString resource "effectiveDateTime" with
        | Some raw -> dates <- addDateFromString param raw dates
        | None -> ()

        match resource["effectivePeriod"] with
        | :? JsonObject as period ->
            let startTs = str period["start"] |> Option.bind parseInstant
            let endTs = str period["end"] |> Option.bind parseInstant

            match startTs, endTs with
            | Some s, Some e -> dates <- addDate param s e dates
            | Some s, None -> dates <- addDate param s (s.AddDays 1.0) dates
            | None, Some e -> dates <- addDate param e e dates
            | _ -> ()
        | _ -> ()

        match FhirJson.propString resource "effectiveInstant" with
        | Some raw -> dates <- addDateFromString param raw dates
        | None -> ()

        dates

    let private applyExtractor
        (resource: FhirResource)
        (param: GeneratedSearchCatalog.ParamDef)
        (acc: IndexRows)
        : IndexRows =
        let indexParam = param.IndexParam

        match param.Extractor with
        | GeneratedSearchCatalog.Extractor.Unsupported _
        | GeneratedSearchCatalog.Extractor.Composite _ -> acc

        | GeneratedSearchCatalog.Extractor.HumanName part ->
            let names = propObjects resource "name"

            let strings =
                names
                |> List.fold
                    (fun sAcc n ->
                        let family = str n["family"] |> Option.defaultValue ""

                        let givens =
                            match n["given"] with
                            | :? JsonArray as arr -> arr |> Seq.choose str |> Seq.toList
                            | _ -> []

                        match part with
                        | "family" -> addString indexParam family sAcc
                        | "given" -> givens |> List.fold (fun a g -> addString indexParam g a) sAcc
                        | _ ->
                            let full = String.concat " " (family :: givens)
                            let withFamily = addString "family" family sAcc

                            let withGivens =
                                givens |> List.fold (fun a g -> addString "given" g a) withFamily

                            addString indexParam full withGivens)
                    acc.Strings

            // When indexing `name`, also emit family/given for Patient.name SP
            { acc with Strings = strings |> List.distinct }

        | GeneratedSearchCatalog.Extractor.StringField path ->
            let strings =
                navigate resource path
                |> List.choose str
                |> List.fold (fun a v -> addString indexParam v a) acc.Strings

            { acc with Strings = strings }

        | GeneratedSearchCatalog.Extractor.Identifier path ->
            let tokens =
                navigate resource path
                |> List.collect (fun node ->
                    match node with
                    | :? JsonObject as obj ->
                        match str obj["value"] with
                        | Some value -> [ addToken indexParam (str obj["system"]) value [] ]
                        | None -> []
                    | :? JsonArray as arr ->
                        objectsOf arr
                        |> List.choose (fun obj ->
                            match str obj["value"] with
                            | Some value -> Some(addToken indexParam (str obj["system"]) value [])
                            | None -> None)
                    | _ -> [])
                |> List.concat

            { acc with Tokens = (tokens @ acc.Tokens) }

        | GeneratedSearchCatalog.Extractor.CodeableConcept path ->
            let tokens =
                navigate resource path
                |> List.collect (fun node ->
                    nestedObjects node "coding"
                    |> List.choose (fun item ->
                        match str item["code"] with
                        | Some code -> Some(addToken indexParam (str item["system"]) code [])
                        | None -> None)
                    |> List.concat)

            { acc with Tokens = tokens @ acc.Tokens }

        | GeneratedSearchCatalog.Extractor.Reference(fields, indexParams) ->
            let refs =
                fields
                |> List.fold
                    (fun rows field ->
                        match resource[field] with
                        | :? JsonObject as obj ->
                            match str obj["reference"] with
                            | Some reference ->
                                indexParams
                                |> List.fold (fun r p -> addRef p reference r) rows
                            | None -> rows
                        | :? JsonArray as arr ->
                            objectsOf arr
                            |> List.fold
                                (fun rows' obj ->
                                    match str obj["reference"] with
                                    | Some reference ->
                                        indexParams
                                        |> List.fold (fun r p -> addRef p reference r) rows'
                                    | None -> rows')
                                rows
                        | _ -> rows)
                    acc.Refs

            { acc with Refs = refs }

        | GeneratedSearchCatalog.Extractor.DateField path ->
            let dates =
                navigate resource path
                |> List.choose str
                |> List.fold (fun a raw -> addDateFromString indexParam raw a) acc.Dates

            { acc with Dates = dates }

        | GeneratedSearchCatalog.Extractor.ObservationEffective ->
            { acc with
                Dates = observationEffectiveDates indexParam resource @ acc.Dates }

        | GeneratedSearchCatalog.Extractor.QuantityValue ->
            let quantities =
                match resource["valueQuantity"] with
                | :? JsonObject as qty ->
                    match qty["value"] with
                    | null -> []
                    | node ->
                        let raw = node.ToJsonString().Trim('"')

                        match Decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) with
                        | true, value ->
                            addQuantity indexParam value (str qty["system"]) (str qty["code"]) []
                        | _ -> []
                | _ -> []

            { acc with Quantities = quantities @ acc.Quantities }

    let extract (resourceType: string) (resource: FhirResource) : IndexRows =
        let defs =
            GeneratedSearchCatalog.forResource resourceType
            |> List.filter (fun p ->
                match p.Kind with
                | GeneratedSearchCatalog.IndexKind.Composite -> false
                | _ -> true)
            // Prefer canonical index writes: skip query aliases that share IndexParam+Extractor
            // with another def (e.g. patient alias of subject) to avoid duplicate work —
            // still write all indexParams from Reference extractor.
            |> List.distinctBy (fun p -> p.IndexParam, sprintf "%A" p.Extractor)

        let rows =
            defs
            |> List.fold (fun acc p -> applyExtractor resource p acc) empty

        { Strings = rows.Strings |> List.distinct
          Tokens = rows.Tokens |> List.distinct
          Refs = rows.Refs |> List.distinct
          Dates = rows.Dates |> List.distinct
          Quantities = rows.Quantities |> List.distinct }
