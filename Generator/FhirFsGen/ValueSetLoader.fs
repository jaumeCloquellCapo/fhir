namespace FhirFsGen

open System
open System.IO
open System.Text.Json
open FhirFsGen.Ir

/// Resolves ValueSet URLs to concrete codes from the FHIR package (compose → CodeSystem).
module ValueSetLoader =

    let canonicalUrl (url: string) =
        match url.IndexOf('|') with
        | -1 -> url.Trim()
        | i -> url.Substring(0, i).Trim()

    let private prop (el: JsonElement) (name: string) =
        match el.TryGetProperty name with
        | true, v -> Some v
        | _ -> None

    let private str (el: JsonElement) (name: string) =
        prop el name
        |> Option.bind (fun v ->
            if v.ValueKind = JsonValueKind.String then Some(v.GetString()) else None)

    let private loadJson (path: string) =
        use doc = JsonDocument.Parse(File.ReadAllText path)
        doc.RootElement.Clone()

    let private conceptCodes (concepts: JsonElement) =
        if concepts.ValueKind <> JsonValueKind.Array then
            []
        else
            let rec walk (el: JsonElement) =
                [
                    match str el "code" with
                    | Some c -> yield c
                    | None -> ()

                    match prop el "concept" with
                    | Some nested when nested.ValueKind = JsonValueKind.Array ->
                        for child in nested.EnumerateArray() do
                            yield! walk child
                    | _ -> ()
                ]

            concepts.EnumerateArray() |> Seq.collect walk |> Seq.distinct |> Seq.toList

    let indexCodeSystems (packageDir: string) : Map<string, string list> =
        Directory.EnumerateFiles(packageDir, "CodeSystem-*.json")
        |> Seq.choose (fun path ->
            try
                let root = loadJson path

                match str root "resourceType", str root "url" with
                | Some "CodeSystem", Some url ->
                    let codes =
                        match prop root "concept" with
                        | Some c -> conceptCodes c
                        | None -> []

                    Some(canonicalUrl url, codes)
                | _ -> None
            with _ ->
                None)
        |> Map.ofSeq

    let private indexValueSetFiles (packageDir: string) : Map<string, string> =
        Directory.EnumerateFiles(packageDir, "ValueSet-*.json")
        |> Seq.choose (fun path ->
            try
                let root = loadJson path

                match str root "resourceType", str root "url" with
                | Some "ValueSet", Some url -> Some(canonicalUrl url, path)
                | _ -> None
            with _ ->
                None)
        |> Map.ofSeq

    let private codesFromCompose (root: JsonElement) (codeSystems: Map<string, string list>) =
        match prop root "compose" |> Option.bind (fun c -> prop c "include") with
        | Some includes when includes.ValueKind = JsonValueKind.Array ->
            includes.EnumerateArray()
            |> Seq.collect (fun inc ->
                let fromConcepts =
                    match prop inc "concept" with
                    | Some concepts -> conceptCodes concepts
                    | None -> []

                let fromSystem =
                    match str inc "system" with
                    | Some system when fromConcepts = [] ->
                        codeSystems
                        |> Map.tryFind (canonicalUrl system)
                        |> Option.defaultValue []
                    | _ -> []

                fromConcepts @ fromSystem)
            |> Seq.distinct
            |> Seq.sort
            |> Seq.toList
        | _ -> []

    let private codesFromExpansion (root: JsonElement) =
        match prop root "expansion" |> Option.bind (fun e -> prop e "contains") with
        | Some contains when contains.ValueKind = JsonValueKind.Array ->
            contains.EnumerateArray()
            |> Seq.choose (fun c -> str c "code")
            |> Seq.distinct
            |> Seq.sort
            |> Seq.toList
        | _ -> []

    let resolve
        (packageDir: string)
        (codeSystems: Map<string, string list>)
        (valueSetFiles: Map<string, string>)
        (url: string)
        : ValueSetCodes option =
        let canon = canonicalUrl url

        match valueSetFiles |> Map.tryFind canon with
        | None -> None
        | Some path ->
            let root = loadJson path
            let codes =
                match codesFromExpansion root with
                | [] -> codesFromCompose root codeSystems
                | expanded -> expanded

            if codes = [] then None
            else Some { Url = canon; Codes = codes }

    let collectRequiredBindings (schemas: ResourceSchema list) =
        let rec walk (el: ElementSchema) =
            [
                match el.Binding with
                | Some { Strength = Required; ValueSet = vs } -> yield vs
                | _ -> ()

                for KeyValue(_, child) in el.Elements do
                    yield! walk child
            ]

        schemas
        |> List.collect (fun s -> s.Elements |> Map.toList |> List.collect (fun (_, e) -> walk e))
        |> List.map canonicalUrl
        |> List.distinct
        |> List.sort

    let resolveMany (packageDir: string) (urls: string list) : ValueSetCodes list =
        let codeSystems = indexCodeSystems packageDir
        let valueSetFiles = indexValueSetFiles packageDir

        urls
        |> List.choose (resolve packageDir codeSystems valueSetFiles)
