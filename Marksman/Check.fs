module Marksman.Check

open System
open System.IO

open Marksman.Config
open Marksman.Paths
open Marksman.Cst
open Marksman.Doc
open Marksman.Folder
open Marksman.Workspace
open Marksman.Diag

[<RequireQualifiedAccess>]
type OutputFormat =
    | Text
    | Json

[<RequireQualifiedAccess>]
type Severity =
    | Error
    | Warning

    override this.ToString() =
        match this with
        | Error -> "error"
        | Warning -> "warning"

type DiagLine = {
    file: string
    line: int
    col: int
    severity: Severity
    code: string
    message: string
}

let private severityOfEntry (el: Element) (entry: Entry) : Severity =
    match entry with
    | NonBreakableWhitespace _ -> Severity.Warning
    | BrokenLink _
    | AmbiguousLink _ ->
        match el with
        | WL _ -> Severity.Error
        | _ -> Severity.Warning

let private messageOfEntry (entry: Entry) : string =
    match entry with
    | BrokenLink(_, ref) -> $"broken link to {refToHuman ref}"
    | AmbiguousLink(_, ref, _) -> $"ambiguous link to {refToHuman ref}"
    | NonBreakableWhitespace _ ->
        "non-breaking whitespace in heading (line won't be interpreted as a heading)"

let private elementOfEntry (entry: Entry) : option<Element> =
    match entry with
    | BrokenLink(el, _)
    | AmbiguousLink(el, _, _) -> Some el
    | NonBreakableWhitespace _ -> None

let private rangeOfEntry (entry: Entry) =
    match entry with
    | BrokenLink(el, _)
    | AmbiguousLink(el, _, _) -> Element.range el
    | NonBreakableWhitespace range -> range

let private toDiagLine (relFile: string) (entry: Entry) : DiagLine =
    let range = rangeOfEntry entry
    let line = range.Start.Line + 1
    let col = range.Start.Character + 1
    let el = elementOfEntry entry

    let severity =
        match el with
        | Some el -> severityOfEntry el entry
        | None -> Severity.Warning

    {
        file = relFile
        line = line
        col = col
        severity = severity
        code = $"MKS{code entry |> int:D3}"
        message = messageOfEntry entry
    }

let private printText (d: DiagLine) =
    printfn $"{d.file}:{d.line}:{d.col}: {d.severity}: {d.message} [{d.code}]"

let private escapeJson (s: string) =
    s
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n")
        .Replace("\r", "\\r")

let private printJson (diags: DiagLine list) =
    let fields (d: DiagLine) =
        $"""  {{"file":"{escapeJson d.file}","line":{d.line},"col":{d.col},"severity":"{d.severity}","code":"{d.code}","message":"{escapeJson d.message}"}}"""

    printfn "["
    let lines = diags |> List.map fields
    printfn "%s" (String.concat ",\n" lines)
    printfn "]"

let check (rootPath: string) (format: OutputFormat) : int =
    // Resolve to absolute path
    let absStr =
        if Path.IsPathRooted(rootPath) then
            rootPath
        else
            Path.GetFullPath(rootPath)

    if not (Directory.Exists(absStr)) then
        eprintfn $"marksman check: path does not exist or is not a directory: {absStr}"
        2
    else
        let absPath = AbsPath.ofSystem absStr
        let folderUri = AbsPath.toUri absPath
        let folderId = UriWith.mkRoot folderUri
        let folderName = Path.GetFileName(absStr)
        let userConfig = Config.read Config.userConfigFile

        match Folder.tryLoad userConfig folderName folderId with
        | None ->
            // No markdown files found — that's fine, not an error
            match format with
            | OutputFormat.Json -> printfn "[]"
            | OutputFormat.Text -> ()

            0
        | Some folder ->
            let workspace = Workspace.ofFolders userConfig [ folder ]
            let mutable allDiags: DiagLine list = []

            for primaryFolder in Workspace.primaryFolders workspace do
                let extraFolders = Workspace.extraFoldersFor primaryFolder workspace
                let folderDiags = Diag.checkFolder primaryFolder extraFolders

                for docId, entries in folderDiags do
                    let doc = Folder.findDocById docId primaryFolder
                    let relFile = Doc.pathFromRoot doc |> RelPath.toSystem

                    for entry in entries do
                        allDiags <- toDiagLine relFile entry :: allDiags

            let allDiags = List.rev allDiags

            match format with
            | OutputFormat.Text ->
                for d in allDiags do
                    printText d
            | OutputFormat.Json -> printJson allDiags

            if List.isEmpty allDiags then 0 else 1
