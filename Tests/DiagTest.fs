module Marksman.DiagTest

open Xunit

open Marksman.Diag
open Marksman.Helpers
open Marksman.Index
open Marksman.Names
open Marksman.Paths
open Marksman.Doc
open Marksman.Folder
open Marksman.Refs

let entryToHuman (entry: Entry) =
    let lsp = diagToLsp entry
    lsp.Message

let diagToHuman (diag: seq<DocId * list<Entry>>) : list<string * string> =
    seq {
        for id, entries in diag do
            for e in entries do
                yield id.Path |> RootedRelPath.relPathForced |> RelPath.toSystem, entryToHuman e
    }
    |> List.ofSeq

[<Fact>]
let documentIndex_1 () =
    let doc = FakeDoc.Mk "# T1\n# T2"

    let titles =
        Doc.index >> Index.titles <| doc
        |> Array.map (fun x -> x.data.title.text)

    Assert.Equal<string>([ "T1"; "T2" ], titles)

[<Fact>]
let nonBreakingWhitespace () =
    let nbsp = "\u00a0"
    let doc = FakeDoc.Mk $"# T1\n##{nbsp}T2"

    match (checkNonBreakingWhitespace doc) with
    | [ NonBreakableWhitespace range ] ->
        Assert.Equal(1, range.Start.Line)
        Assert.Equal(1, range.End.Line)

        Assert.Equal(2, range.Start.Character)
        Assert.Equal(3, range.End.Character)
    | _ -> failwith "Expected NonBreakingWhitespace diagnostic"

[<Fact>]
let noDiagOnShortcutLinks () =
    let doc = FakeDoc.Mk([| "# H1"; "## H2"; "[shortcut]"; "[[#h42]]" |])
    let folder = FakeFolder.Mk([ doc ])
    let diag = checkFolder folder |> diagToHuman

    Assert.Equal<string * string>([ "fake.md", "Link to non-existent heading 'h42'" ], diag)

[<Fact>]
let noDiagOnRealUrls () =
    let doc =
        FakeDoc.Mk([| "# H1"; "## H2"; "[](www.bad.md)"; "[](https://www.good.md)" |])

    let folder = FakeFolder.Mk([ doc ])
    let diag = checkFolder folder |> diagToHuman

    Assert.Equal<string * string>([ "fake.md", "Link to non-existent document 'www.bad.md'" ], diag)

[<Fact>]
let noDiagOnNonMarkdownFiles () =
    let doc =
        FakeDoc.Mk(
            [|
                "# H1"
                "## H2"
                "[](bad.md)"
                "[](another%20bad.md)"
                "[](good/folder)"
            |]
        )

    let folder = FakeFolder.Mk([ doc ])
    let diag = checkFolder folder |> diagToHuman

    Assert.Equal<string * string>(
        [
            "fake.md", "Link to non-existent document 'bad.md'"
            "fake.md", "Link to non-existent document 'another bad.md'"
        ],
        diag
    )


[<Fact>]
let crossFileDiagOnBrokenWikiLinks () =
    let doc = FakeDoc.Mk([| "[[bad]]" |])

    let folder = FakeFolder.Mk([ doc ])
    let diag = checkFolder folder |> diagToHuman

    Assert.Equal<string * string>([ "fake.md", "Link to non-existent document 'bad'" ], diag)

[<Fact>]
let noCrossFileDiagOnSingleFileFolders () =
    let doc =
        FakeDoc.Mk(
            [|
                "[](bad.md)" //
                "[[another-bad]]"
                "[bad-ref][bad-ref]"
            |]
        )

    let folder = Folder.singleFile doc None
    let diag = checkFolder folder |> diagToHuman

    Assert.Equal<string * string>(
        [
            "fake.md", "Link to non-existent link definition with the label 'bad-ref'"
        ],
        diag
    )

[<Fact>]
let noDiagForExistingAttachment () =
    let doc = FakeDoc.Mk([| "[[image.png]]" |])
    let folder = FakeFolder.Mk([ doc ]) |> Folder.withAttachment (RelPath "image.png")
    let diag = checkFolder folder |> diagToHuman

    Assert.Equal<string * string>([], diag)

[<Fact>]
let diagForMissingAttachment () =
    let doc = FakeDoc.Mk([| "[[image.png]]" |])
    let folder = FakeFolder.Mk([ doc ])
    let diag = checkFolder folder |> diagToHuman

    Assert.Equal<string * string>(
        [ "fake.md", "Link to non-existent document 'image.png'" ],
        diag
    )

[<Fact>]
let noDiagForEmbedLinkToAttachmentExtMissing () =
    // ![[image.png]] — Markdig does not parse ![[...]] as a WikiLink (the ! causes it to be
    // treated as an image directive). So no wiki-link diagnostic is produced regardless.
    // This test documents that behavior.
    let doc = FakeDoc.Mk([| "![[image.png]]" |])
    let folder = FakeFolder.Mk([ doc ])
    let diag = checkFolder folder |> diagToHuman

    Assert.Equal<string * string>([], diag)

[<Fact>]
let diagForMissingAttachmentWithMdExt () =
    // [[something.md]] (not embed) — a wiki link pointing to a non-existent markdown file
    // should still show a broken-link diagnostic
    let doc = FakeDoc.Mk([| "[[something.md]]" |])
    let folder = FakeFolder.Mk([ doc ])
    let diag = checkFolder folder |> diagToHuman

    Assert.Equal<string * string>(
        [ "fake.md", "Link to non-existent document 'something.md'" ],
        diag
    )

[<Fact>]
let destAttachmentForExistingFile () =
    let doc = FakeDoc.Mk([| "[[diagram.pdf]]" |])
    let folder = FakeFolder.Mk([ doc ]) |> Folder.withAttachment (RelPath "diagram.pdf")

    let links = Doc.index doc |> Index.links |> Array.ofSeq
    let link = links |> Array.head
    let dests = Dest.tryResolveElement folder doc link |> Array.ofSeq

    match dests with
    | [| Dest.Attachment(relPath, _) |] ->
        Assert.Equal("diagram.pdf", RelPath.toSystem relPath)
    | _ -> failwith $"Expected Dest.Attachment, got: {dests}"
