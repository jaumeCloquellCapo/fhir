namespace FhirServer.Domain.Services

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types

/// Extract FHIR search index rows from a resource (mirrors SearchEngine plan coverage).
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

    let private addRefFromFields (resource: FhirResource) (fields: string list) (params': string list) (acc: RefRow list) =
        fields
        |> List.fold
            (fun rows field ->
                match resource[field] with
                | :? JsonObject as obj ->
                    match str obj["reference"] with
                    | Some reference ->
                        params'
                        |> List.fold (fun r p -> addRef p reference r) rows
                    | None -> rows
                | _ -> rows)
            acc

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
            // Day-precision strings get an exclusive end of next day when date-only.
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

    let private observationDates (resource: FhirResource) =
        let mutable dates: DateRow list = []

        match FhirJson.propString resource "effectiveDateTime" with
        | Some raw -> dates <- addDateFromString "date" raw dates
        | None -> ()

        match resource["effectivePeriod"] with
        | :? JsonObject as period ->
            let startTs = str period["start"] |> Option.bind parseInstant
            let endTs = str period["end"] |> Option.bind parseInstant

            match startTs, endTs with
            | Some s, Some e -> dates <- addDate "date" s e dates
            | Some s, None -> dates <- addDate "date" s (s.AddDays 1.0) dates
            | None, Some e -> dates <- addDate "date" e e dates
            | _ -> ()
        | _ -> ()

        match FhirJson.propString resource "effectiveInstant" with
        | Some raw -> dates <- addDateFromString "date" raw dates
        | None -> ()

        dates

    let private observationQuantities (resource: FhirResource) =
        match resource["valueQuantity"] with
        | :? JsonObject as qty ->
            match qty["value"] with
            | null -> []
            | node ->
                let raw = node.ToJsonString().Trim('"')

                match Decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) with
                | true, value ->
                    addQuantity "value-quantity" value (str qty["system"]) (str qty["code"]) []
                | _ -> []
        | _ -> []

    let extract (resourceType: string) (resource: FhirResource) : IndexRows =
        match resourceType with
        | "Patient" ->
            let names = propObjects resource "name"

            let strings =
                names
                |> List.fold
                    (fun acc n ->
                        let family = str n["family"] |> Option.defaultValue ""

                        let givens =
                            match n["given"] with
                            | :? JsonArray as arr -> arr |> Seq.choose str |> Seq.toList
                            | _ -> []

                        let withFamily = addString "family" family acc

                        let withGivens =
                            givens |> List.fold (fun a g -> addString "given" g a) withFamily

                        let full = String.concat " " (family :: givens)
                        addString "name" full withGivens)
                    []

            let strings =
                match FhirJson.propString resource "gender" with
                | Some g -> addString "gender" g strings
                | None -> strings

            let dates =
                match FhirJson.propString resource "birthDate" with
                | Some bd -> addDateFromString "birthdate" bd []
                | None -> []

            let tokens =
                propObjects resource "identifier"
                |> List.fold
                    (fun acc item ->
                        match str item["value"] with
                        | Some value -> addToken "identifier" (str item["system"]) value acc
                        | None -> acc)
                    []

            { Strings = strings |> List.distinct
              Tokens = tokens |> List.distinct
              Refs = []
              Dates = dates |> List.distinct
              Quantities = [] }

        | "Observation" ->
            let strings =
                match FhirJson.propString resource "status" with
                | Some s -> addString "status" s []
                | None -> []

            let tokens =
                nestedObjects resource["code"] "coding"
                |> List.fold
                    (fun acc item ->
                        match str item["code"] with
                        | Some code -> addToken "code" (str item["system"]) code acc
                        | None -> acc)
                    []

            let refs = addRefFromFields resource [ "subject" ] [ "patient"; "subject" ] []

            { Strings = strings |> List.distinct
              Tokens = tokens |> List.distinct
              Refs = refs |> List.distinct
              Dates = observationDates resource |> List.distinct
              Quantities = observationQuantities resource |> List.distinct }

        | "Encounter"
        | "Condition"
        | "AllergyIntolerance"
        | "MedicationRequest"
        | "Procedure"
        | "DiagnosticReport"
        | "Immunization"
        | "CarePlan"
        | "DocumentReference" ->
            let refs =
                addRefFromFields resource [ "subject"; "patient" ] [ "patient"; "subject" ] []

            { Strings = []
              Tokens = []
              Refs = refs |> List.distinct
              Dates = []
              Quantities = [] }

        | _ -> empty
