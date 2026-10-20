namespace FhirFsGen

open System
open System.IO
open System.Text
open System.Text.Json
open FhirFsGen.Ir

/// Emits FHIR-Schema-inspired JSON (nested elements, array, required, binding).
module EmitSchemaJson =

    let private strengthString =
        function
        | BindingStrengthKind.Required -> "required"
        | BindingStrengthKind.Extensible -> "extensible"
        | BindingStrengthKind.Preferred -> "preferred"
        | BindingStrengthKind.Example -> "example"

    let private writeConstraints (writer: Utf8JsonWriter) (constraints: ElementConstraint list) =
        if constraints <> [] then
            writer.WritePropertyName("constraints")
            writer.WriteStartArray()

            for c in constraints do
                writer.WriteStartObject()
                writer.WriteString("key", c.Key)
                writer.WriteString("severity", c.Severity)
                writer.WriteString("expression", c.Expression)

                if not (String.IsNullOrWhiteSpace c.Human) then
                    writer.WriteString("human", c.Human)

                writer.WriteEndObject()

            writer.WriteEndArray()

    let rec private writeElement (writer: Utf8JsonWriter) (el: ElementSchema) =
        writer.WriteStartObject()
        writer.WriteString("path", el.Path)
        writer.WriteNumber("min", el.Min)

        match el.Max with
        | None -> writer.WriteString("max", "*")
        | Some n -> writer.WriteNumber("max", n)

        writer.WriteBoolean("array", el.Array)

        writer.WritePropertyName("type")
        writer.WriteStartArray()

        for t in el.Types do
            writer.WriteStringValue(t)

        writer.WriteEndArray()

        match el.Binding with
        | Some b ->
            writer.WritePropertyName("binding")
            writer.WriteStartObject()
            writer.WriteString("strength", strengthString b.Strength)
            writer.WriteString("valueSet", b.ValueSet)
            writer.WriteEndObject()
        | None -> ()

        match el.Choices with
        | Some fields when fields <> [] ->
            writer.WritePropertyName("choices")
            writer.WriteStartArray()

            for f in fields do
                writer.WriteStringValue(f)

            writer.WriteEndArray()
        | _ -> ()

        match el.ContentReference with
        | Some cref when not (String.IsNullOrWhiteSpace cref) ->
            writer.WriteString("contentReference", cref)
        | _ -> ()

        writeConstraints writer el.Constraints

        if not (Map.isEmpty el.Elements) then
            writer.WritePropertyName("elements")
            writer.WriteStartObject()

            for KeyValue(name, child) in el.Elements do
                writer.WritePropertyName(name)
                writeElement writer child

            writer.WriteEndObject()

        writer.WriteEndObject()

    let writeResource (schema: ResourceSchema) (path: string) =
        let dir = Path.GetDirectoryName path

        if not (String.IsNullOrEmpty dir) then
            Directory.CreateDirectory dir |> ignore

        use stream = File.Create path
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))

        writer.WriteStartObject()
        writer.WriteString("fhirVersion", schema.FhirVersion)
        writer.WriteString("url", schema.Url)
        writer.WriteString("type", schema.TypeName)
        writer.WriteString("name", schema.TypeName)
        writer.WriteString("kind", schema.Kind)

        let derivation =
            if String.IsNullOrWhiteSpace schema.Derivation then
                "specialization"
            else
                schema.Derivation

        writer.WriteString("derivation", derivation)

        writer.WritePropertyName("required")
        writer.WriteStartArray()

        for r in schema.Required do
            writer.WriteStringValue(r)

        writer.WriteEndArray()

        writeConstraints writer schema.Constraints

        writer.WritePropertyName("elements")
        writer.WriteStartObject()

        for KeyValue(name, el) in schema.Elements do
            writer.WritePropertyName(name)
            writeElement writer el

        writer.WriteEndObject()
        writer.WriteEndObject()
        writer.Flush()

    /// canonical URL → relative schema file name under schemas/profiles/.
    let writeProfilesIndex (entries: (string * string) list) (path: string) =
        let dir = Path.GetDirectoryName path

        if not (String.IsNullOrEmpty dir) then
            Directory.CreateDirectory dir |> ignore

        let options = JsonSerializerOptions(WriteIndented = true)
        let dto = entries |> dict
        let utf8 = UTF8Encoding(false)
        File.WriteAllText(path, JsonSerializer.Serialize(dto, options), utf8)

    let writeManifest (manifest: GenerationManifest) (path: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

        let options = JsonSerializerOptions(WriteIndented = true)

        let dto =
            {| fhirVersion = manifest.FhirVersion
               generatedAtUtc = manifest.GeneratedAtUtc
               resources = manifest.Resources |> List.map (fun r -> r.TypeName) |}

        let json = JsonSerializer.Serialize(dto, options)
        File.WriteAllText(path, json, Encoding.UTF8)

    /// url → codes map for required bindings (consumed by GeneratedSchemaValidator).
    let writeValueSets (sets: ValueSetCodes list) (path: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let options = JsonSerializerOptions(WriteIndented = true)

        let dto =
            sets
            |> List.map (fun s -> s.Url, s.Codes)
            |> dict

        let utf8 = UTF8Encoding(false)
        File.WriteAllText(path, JsonSerializer.Serialize(dto, options), utf8)
