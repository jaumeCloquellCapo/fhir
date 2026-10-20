namespace FhirServer.Infrastructure.Http

open System
open System.IO

/// Minimal `.env` loader: KEY=VALUE lines into process environment (does not overwrite existing vars).
module DotEnv =

    let private parseLine (line: string) =
        let trimmed = line.Trim()

        if trimmed.Length = 0 || trimmed.StartsWith('#') then
            None
        else
            match trimmed.Split('=', 2) with
            | [| key; value |] when not (String.IsNullOrWhiteSpace key) ->
                let k = key.Trim()
                let v = value.Trim().Trim('"').Trim('\'')
                Some(k, v)
            | _ -> None

    let loadFile (path: string) =
        if File.Exists path then
            for line in File.ReadLines path do
                match parseLine line with
                | Some(key, value) when String.IsNullOrEmpty(Environment.GetEnvironmentVariable key) ->
                    Environment.SetEnvironmentVariable(key, value)
                | _ -> ()

    /// Loads `.env.dev` then `.env` from the given directory (later file only fills unset keys).
    let loadFromDirectory (dir: string) =
        loadFile (Path.Combine(dir, ".env.dev"))
        loadFile (Path.Combine(dir, ".env"))
