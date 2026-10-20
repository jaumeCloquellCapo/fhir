namespace FhirServer.Application.Terminology

open System
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Ports
open FhirServer.Domain.Types

/// Local terminology ops over persisted CodeSystem / ValueSet resources.
module TerminologyOps =

    type Coding = {
        System: string
        Code: string
        Display: string option
    }

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

    let private paramValue (parameters: FhirResource option) (query: Map<string, string list>) (name: string) =
        let fromQuery = query |> Map.tryFind name |> Option.bind List.tryHead

        let fromBody =
            match parameters with
            | None -> None
            | Some p ->
                propObjects p "parameter"
                |> List.tryPick (fun item ->
                    match str item["name"] with
                    | Some n when n = name ->
                        str item["valueString"]
                        |> Option.orElse (str item["valueCode"])
                        |> Option.orElse (str item["valueUri"])
                        |> Option.orElse (
                            match item["valueCoding"] with
                            | :? JsonObject as coding -> str coding["code"]
                            | _ -> None
                        )
                    | _ -> None)

        fromBody |> Option.orElse fromQuery

    let private findCodeSystemByUrl (store: IResourceStore) (url: string) =
        store.ListByType "CodeSystem"
        |> List.tryFind (fun cs -> FhirJson.propString cs "url" = Some url)

    let rec private conceptCodings (system: string) (conceptNode: JsonNode) : Coding list =
        match conceptNode with
        | :? JsonObject as concept ->
            match str concept["code"] with
            | None -> []
            | Some code ->
                let display = str concept["display"]

                let self = {
                    System = system
                    Code = code
                    Display = display
                }

                let children =
                    match concept["concept"] with
                    | :? JsonArray as arr ->
                        arr |> Seq.collect (conceptCodings system) |> Seq.toList
                    | _ -> []

                self :: children
        | _ -> []

    let private codeSystemCodings (cs: FhirResource) =
        match FhirJson.propString cs "url" with
        | None -> []
        | Some system ->
            match cs["concept"] with
            | :? JsonArray as arr -> arr |> Seq.collect (conceptCodings system) |> Seq.toList
            | _ -> []

    let private includeCodings (store: IResourceStore) (inc: JsonObject) : Coding list =
        let system = str inc["system"] |> Option.defaultValue ""

        let explicitConcepts =
            match inc["concept"] with
            | :? JsonArray as arr ->
                arr
                |> Seq.choose (fun (n: JsonNode) ->
                    match n with
                    | :? JsonObject as c ->
                        match str c["code"] with
                        | Some code ->
                            Some {
                                System = system
                                Code = code
                                Display = str c["display"]
                            }
                        | None -> None
                    | _ -> None)
                |> Seq.toList
            | _ -> []

        if explicitConcepts <> [] then
            explicitConcepts
        elif system = "" then
            []
        else
            match findCodeSystemByUrl store system with
            | Some cs -> codeSystemCodings cs
            | None -> []

    let private expandValueSet (store: IResourceStore) (valueSet: FhirResource) : Coding list =
        match valueSet["compose"] with
        | :? JsonObject as compose ->
            match compose["include"] with
            | :? JsonArray as includes ->
                includes
                |> Seq.choose (fun (n: JsonNode) ->
                    match n with
                    | :? JsonObject as inc -> Some(includeCodings store inc)
                    | _ -> None)
                |> Seq.concat
                |> Seq.distinctBy (fun c -> c.System, c.Code)
                |> Seq.toList
            | _ -> []
        | _ ->
            // Already expanded?
            match valueSet["expansion"] with
            | :? JsonObject as expansion ->
                match expansion["contains"] with
                | :? JsonArray as contains ->
                    contains
                    |> Seq.choose (fun (n: JsonNode) ->
                        match n with
                        | :? JsonObject as c ->
                            match str c["code"] with
                            | Some code ->
                                Some {
                                    System = str c["system"] |> Option.defaultValue ""
                                    Code = code
                                    Display = str c["display"]
                                }
                            | None -> None
                        | _ -> None)
                    |> Seq.toList
                | _ -> []
            | _ -> []

    let private withExpansion (valueSet: FhirResource) (codings: Coding list) =
        let clone = FhirJson.deepClone valueSet
        let contains = JsonArray()

        for c in codings do
            let item =
                FhirJson.mkObject [
                    yield "system", FhirJson.jStr c.System
                    yield "code", FhirJson.jStr c.Code

                    match c.Display with
                    | Some d -> yield "display", FhirJson.jStr d
                    | None -> ()
                ]

            contains.Add(item)

        let expansion =
            FhirJson.mkObject [
                "timestamp", FhirJson.jStr (DateTimeOffset.UtcNow.ToString("o"))
                "total", FhirJson.jInt codings.Length
                "contains", contains
            ]

        clone["expansion"] <- expansion
        clone

    let private resolveValueSet
        (store: IResourceStore)
        (id: string option)
        (url: string option)
        (bodyVs: FhirResource option)
        =
        match bodyVs with
        | Some vs when FhirJson.resourceType vs = "ValueSet" -> Ok vs
        | Some _ -> Error(BadRequest "Body must be a ValueSet resource.")
        | None ->
            match id with
            | Some vid ->
                match store.Get("ValueSet", vid) with
                | Some vs -> Ok vs
                | None -> Error(NotFound("ValueSet", vid))
            | None ->
                match url with
                | Some u ->
                    match
                        store.ListByType "ValueSet"
                        |> List.tryFind (fun vs -> FhirJson.propString vs "url" = Some u)
                    with
                    | Some vs -> Ok vs
                    | None -> Error(BadRequest $"ValueSet url '{u}' not found.")
                | None -> Error(BadRequest "ValueSet id, url, or ValueSet body is required.")

    let expand
        (store: IResourceStore)
        (id: string option)
        (query: Map<string, string list>)
        (body: FhirResource option)
        : FhirResult<FhirResource> =
        let url = paramValue body query "url"
        let vsBody =
            match body with
            | Some b when FhirJson.resourceType b = "ValueSet" -> Some b
            | Some b when FhirJson.resourceType b = "Parameters" -> None
            | other -> other

        resolveValueSet store id url vsBody
        |> Result.map (fun vs ->
            let codings = expandValueSet store vs
            withExpansion vs codings)

    let lookup
        (store: IResourceStore)
        (query: Map<string, string list>)
        (body: FhirResource option)
        : FhirResult<FhirResource> =
        let system = paramValue body query "system"
        let code = paramValue body query "code"

        match system, code with
        | None, _
        | _, None -> Error(BadRequest "lookup requires system and code.")
        | Some sys, Some codeValue ->
            match findCodeSystemByUrl store sys with
            | None -> Error(BadRequest $"CodeSystem '{sys}' not found.")
            | Some cs ->
                match
                    codeSystemCodings cs
                    |> List.tryFind (fun c -> c.Code = codeValue)
                with
                | None -> Error(BadRequest $"Code '{codeValue}' not found in CodeSystem '{sys}'.")
                | Some coding ->
                    let parameters = JsonArray()

                    parameters.Add(
                        FhirJson.mkObject [
                            "name", FhirJson.jStr "name"
                            "valueString", FhirJson.jStr (FhirJson.propString cs "name" |> Option.defaultValue sys)
                        ]
                    )

                    parameters.Add(
                        FhirJson.mkObject [ "name", FhirJson.jStr "display"; "valueString", FhirJson.jStr (coding.Display |> Option.defaultValue codeValue) ]
                    )

                    parameters.Add(
                        FhirJson.mkObject [
                            "name", FhirJson.jStr "version"
                            "valueString",
                            FhirJson.jStr (FhirJson.propString cs "version" |> Option.defaultValue "")
                        ]
                    )

                    Ok(
                        FhirJson.mkObject [
                            "resourceType", FhirJson.jStr "Parameters"
                            "parameter", parameters
                        ]
                    )

    let validateCode
        (store: IResourceStore)
        (id: string option)
        (query: Map<string, string list>)
        (body: FhirResource option)
        : FhirResult<FhirResource> =
        let url = paramValue body query "url"
        let code = paramValue body query "code"
        let system = paramValue body query "system"

        let vsBody =
            match body with
            | Some b when FhirJson.resourceType b = "ValueSet" -> Some b
            | _ -> None

        match code with
        | None -> Error(BadRequest "validate-code requires code.")
        | Some codeValue ->
            resolveValueSet store id url vsBody
            |> Result.map (fun vs ->
                let codings = expandValueSet store vs

                let ok =
                    codings
                    |> List.exists (fun c ->
                        c.Code = codeValue
                        && (match system with
                            | None -> true
                            | Some s -> c.System = s))

                let parameters = JsonArray()

                parameters.Add(
                    FhirJson.mkObject [ "name", FhirJson.jStr "result"; "valueBoolean", FhirJson.jBool ok ]
                )

                parameters.Add(
                    FhirJson.mkObject [ "name", FhirJson.jStr "code"; "valueCode", FhirJson.jStr codeValue ]
                )

                match system with
                | Some s ->
                    parameters.Add(
                        FhirJson.mkObject [ "name", FhirJson.jStr "system"; "valueUri", FhirJson.jStr s ]
                    )
                | None -> ()

                FhirJson.mkObject [
                    "resourceType", FhirJson.jStr "Parameters"
                    "parameter", parameters
                ])
