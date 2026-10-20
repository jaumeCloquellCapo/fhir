namespace FhirFsGen

open System
open System.IO
open System.Text.Json

/// Loads StructureDefinitions as raw JSON from an HL7 FHIR package (no Firely).
module StructureLoader =

    type StructureDefinitionDoc =
        { Json: JsonElement
          TypeName: string
          Url: string
          Kind: string
          Abstract: bool
          Derivation: string
          Path: string }

    let private prop (el: JsonElement) (name: string) =
        match el.TryGetProperty name with
        | true, v -> Some v
        | _ -> None

    let private str (el: JsonElement) (name: string) =
        prop el name
        |> Option.bind (fun v ->
            if v.ValueKind = JsonValueKind.String then Some(v.GetString()) else None)

    let private boolOrFalse (el: JsonElement) (name: string) =
        prop el name
        |> Option.map (fun v -> v.ValueKind = JsonValueKind.True)
        |> Option.defaultValue false

    let tryLoadFile (path: string) =
        let json = File.ReadAllText path
        use doc = JsonDocument.Parse json
        let root = doc.RootElement.Clone()

        match str root "resourceType" with
        | Some "StructureDefinition" ->
            let typeName = str root "type" |> Option.defaultValue ""
            let url = str root "url" |> Option.defaultValue ""
            let kind = str root "kind" |> Option.defaultValue ""
            let derivation = str root "derivation" |> Option.defaultValue ""

            Some
                { Json = root
                  TypeName = typeName
                  Url = url
                  Kind = kind
                  Abstract = boolOrFalse root "abstract"
                  Derivation = derivation
                  Path = path }
        | _ -> None

    let loadStructureDefinition (packageDir: string) (resourceType: string) =
        let path = Path.Combine(packageDir, $"StructureDefinition-{resourceType}.json")

        if not (File.Exists path) then
            failwith $"Missing {path}"

        match tryLoadFile path with
        | Some sd -> sd
        | None -> failwith $"Not a StructureDefinition: {path}"

    let private isConcreteSpecialization (sd: StructureDefinitionDoc) =
        sd.Derivation = "specialization"
        && not sd.Abstract
        && not (String.IsNullOrWhiteSpace sd.TypeName)

    /// Concrete resource types (kind=resource, specialization, not abstract).
    let listConcreteResourceTypes (packageDir: string) =
        Directory.EnumerateFiles(packageDir, "StructureDefinition-*.json")
        |> Seq.choose tryLoadFile
        |> Seq.filter (fun sd -> sd.Kind = "resource" && isConcreteSpecialization sd)
        |> Seq.map (fun sd -> sd.TypeName)
        |> Seq.distinct
        |> Seq.sort
        |> Seq.toList

    /// Concrete complex data types (HumanName, CodeableConcept, Extension, …).
    let listConcreteComplexTypes (packageDir: string) =
        Directory.EnumerateFiles(packageDir, "StructureDefinition-*.json")
        |> Seq.choose tryLoadFile
        |> Seq.filter (fun sd -> sd.Kind = "complex-type" && isConcreteSpecialization sd)
        |> Seq.map (fun sd -> sd.TypeName)
        |> Seq.distinct
        |> Seq.sort
        |> Seq.toList
