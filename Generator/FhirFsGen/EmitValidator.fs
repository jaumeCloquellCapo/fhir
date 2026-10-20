namespace FhirFsGen

open System
open System.IO
open System.Text

/// Emits the Firely-free JSON schema validator the server compiles.
module EmitValidator =

    let private templateFileName = "ValidatorTemplate.fs"

    let private findTemplate () =
        let candidates =
            [
                Path.Combine(AppContext.BaseDirectory, templateFileName)
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", templateFileName)
                Path.Combine("generator", "FhirFsGen", templateFileName)
                Path.Combine(__SOURCE_DIRECTORY__, templateFileName)
            ]

        candidates
        |> List.map Path.GetFullPath
        |> List.tryFind File.Exists
        |> Option.defaultWith (fun () ->
            let searched = String.Join("; ", candidates)
            failwith $"Missing {templateFileName}. Searched: {searched}")

    let writeSchemaValidatorFs (path: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let template = File.ReadAllText(findTemplate(), Encoding.UTF8)
        File.WriteAllText(path, template.TrimStart() + "\n", Encoding.UTF8)
