namespace FhirServer.Domain.Services

open System
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types

/// Extract FHIR search index rows from a resource (mirrors SearchEngine.matchesQuery coverage).
module SearchIndexer =

    type StringRow = { Param: string; Value: string }

    type TokenRow =
        { Param: string
          System: string
          Code: string }

    type RefRow = { Param: string; Target: string }

    type IndexRows =
        { Strings: StringRow list
          Tokens: TokenRow list
          Refs: RefRow list }

    let empty: IndexRows =
        { Strings = []
          Tokens = []
          Refs = [] }

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

            let strings =
                match FhirJson.propString resource "birthDate" with
                | Some bd -> addString "birthdate" bd strings
                | None -> strings

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
              Refs = [] }

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
              Refs = refs |> List.distinct }

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
              Refs = refs |> List.distinct }

        | _ -> empty
