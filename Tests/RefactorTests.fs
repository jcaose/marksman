module Marksman.RefactorTests

open System.IO
open Ionide.LanguageServerProtocol.Types
open Xunit
open Misc
open Marksman.Paths

let editsByFile =
    function
    | Refactor.Edit wsEdit ->
        match wsEdit.DocumentChanges with
        | Some docChanges ->
            docChanges
            |> Array.map (fun docChange ->
                let docEdit =
                    match docChange with
                    | TextDocumentEdit docEdit -> docEdit
                    | _ -> failwith $"Refactoring should always produce TextDocumentEdits"

                let doc = Path.GetFileName(docEdit.TextDocument.Uri)
                let ranges = docEdit.Edits |> Array.map (fun x -> x.Range, x.NewText)
                doc, ranges)
            |> Map.ofArray
        | _ ->
            match wsEdit.Changes with
            | Some docEditMap ->
                Map.toSeq docEditMap
                |> Seq.map (fun (doc, edits) ->
                    let ranges = edits |> Array.map (fun x -> x.Range, x.NewText)
                    (Path.GetFileName doc), ranges)
                |> Map.ofSeq
            | _ -> Map.empty
    | other -> failwith ($"Edit ranges are not defined for: {other}")

/// Extract (oldFilename, newFilename) from the RenameFile document change, if present.
let attachmentRenameChange =
    function
    | Refactor.Edit { DocumentChanges = Some docChanges } ->
        docChanges
        |> Array.tryPick (function
            | RenameFile rf -> Some(Path.GetFileName(rf.oldUri), Path.GetFileName(rf.newUri))
            | _ -> None)
    | _ -> None

/// Extract text-edit ranges for a named file from the workspace edit produced by a rename.
let attachmentLinkEdits filename =
    function
    | Refactor.Edit { DocumentChanges = Some docChanges } ->
        docChanges
        |> Array.choose (function
            | TextDocumentEdit e when Path.GetFileName(e.TextDocument.Uri) = filename ->
                Some(e.Edits |> Array.map (fun x -> x.Range, x.NewText))
            | _ -> None)
        |> Array.concat
    | other -> failwith $"Expected Edit, got: {other}"

module RenameTests =
    module ReferenceLinks =
        let mkWorkspace () =
            let doc1 =
                Helpers.FakeDoc.Mk(
                    //  0         1         2
                    //  0123456789012345678901234567890
                    [|
                        "# Doc 1"
                        "Start [lbl1], then [lbl2]."
                        "Then [lbl1] again."
                        "And [broken] label."
                        ""
                        "[lbl1]: https://url1.com"
                        "[lbl2]: https://url2.com"
                    |],
                    path = "doc1.md"
                )

            let folder = Helpers.FakeFolder.Mk([ doc1 ])
            doc1, folder


        [<Fact>]
        let onRefLabel () =
            let pos = Position.Mk(2, 7)
            let doc1, folder = mkWorkspace ()
            let res = Refactor.rename true folder doc1 pos "newLbl"

            let expectedRanges =
                Map.ofSeq [
                    "doc1.md",
                    [|
                        Range.Mk(5, 1, 5, 5), "newLbl"
                        Range.Mk(2, 6, 2, 10), "newLbl"
                        Range.Mk(1, 7, 1, 11), "newLbl"
                    |]
                ]

            let actualRanges = editsByFile res

            Assert.Equal<Range * string>(
                Map.find "doc1.md" expectedRanges,
                Map.find "doc1.md" actualRanges
            )

        [<Fact>]
        let onDefLabel () =
            let pos = Position.Mk(5, 3)
            let doc1, folder = mkWorkspace ()
            let res = Refactor.rename true folder doc1 pos "newLbl"

            let expectedRanges =
                Map.ofSeq [
                    "doc1.md",
                    [|
                        Range.Mk(5, 1, 5, 5), "newLbl"
                        Range.Mk(2, 6, 2, 10), "newLbl"
                        Range.Mk(1, 7, 1, 11), "newLbl"
                    |]
                ]

            let actualRanges = editsByFile res

            Assert.Equal<Range * string>(
                Map.find "doc1.md" expectedRanges,
                Map.find "doc1.md" actualRanges
            )

    module HeadingLinks =
        let mkWorkspace () =
            let doc1 =
                Helpers.FakeDoc.Mk(
                    //   0         1         2
                    //   0123456789012345678901234567890
                    [|
                        "# Doc 1"
                        "## Doc 1.2"
                        "## Doc 1.3"
                        "See [[#doc-12]]"
                        "Also [](#doc-12)"
                    |],
                    path = "doc1.md"
                )

            let doc2 =
                Helpers.FakeDoc.Mk(
                    //   0         1         2
                    //   0123456789012345678901234567890
                    [|
                        "# Doc 2"
                        "[[doc-1]]"
                        "[[doc-1#doc-12]]"
                        "[](/doc1.md#doc-12)"
                        // filename wiki-link
                        "[[doc1]]"
                    |],
                    path = "doc2.md"
                )

            let folder = Helpers.FakeFolder.Mk([ doc1; doc2 ])
            doc1, doc2, folder


        [<Fact>]
        let onTitle () =
            let pos = Position.Mk(0, 3)
            let doc1, _doc2, folder = mkWorkspace ()
            let res = Refactor.rename true folder doc1 pos "New Title"

            let expectedRanges =
                Map.ofSeq [
                    "doc1.md", [| Range.Mk(0, 2, 0, 7), "New Title" |]
                    "doc2.md",
                    [|
                        Range.Mk(2, 2, 2, 7), "new-title"
                        Range.Mk(1, 2, 1, 7), "new-title"
                    |]
                ]

            let actualRanges = editsByFile res

            Assert.Equal<Range * string>(
                Map.find "doc1.md" expectedRanges,
                Map.find "doc1.md" actualRanges
            )

            Assert.Equal<Range * string>(
                Map.find "doc2.md" expectedRanges,
                Map.find "doc2.md" actualRanges
            )

        [<Fact>]
        let onSubtitle () =
            let pos = Position.Mk(1, 5)
            let doc1, _doc2, folder = mkWorkspace ()
            let res = Refactor.rename true folder doc1 pos "New Title"

            let expectedRanges =
                Map.ofSeq [
                    "doc1.md",
                    [|
                        Range.Mk(4, 9, 4, 15), "new-title"
                        Range.Mk(3, 7, 3, 13), "New Title"
                        Range.Mk(1, 3, 1, 10), "New Title"
                    |]
                    "doc2.md",
                    [|
                        Range.Mk(3, 12, 3, 18), "new-title"
                        Range.Mk(2, 8, 2, 14), "New Title"
                    |]
                ]

            let actualRanges = editsByFile res

            Assert.Equal<Range * string>(
                Map.find "doc1.md" expectedRanges,
                Map.find "doc1.md" actualRanges
            )

            Assert.Equal<Range * string>(
                Map.find "doc2.md" expectedRanges,
                Map.find "doc2.md" actualRanges
            )

module AttachmentRename =
    open Marksman.Folder

    /// Build a folder with one doc and one registered attachment.
    let mkFolderWithAttachment (docContent: string) (attachRelPath: string) =
        let doc = Helpers.FakeDoc.Mk(docContent, path = "doc.md")

        let folder =
            Helpers.FakeFolder.Mk([ doc ])
            |> Folder.withAttachment (RelPath attachRelPath)

        doc, folder

    // ─── Extension handling ───────────────────────────────────────────────────

    [<Fact>]
    let extensionPreservedWhenUserProvidesStem () =
        // User provides "newdiagram" (no extension) → file should become "newdiagram.pdf"
        let doc, folder = mkFolderWithAttachment "[[diagram.pdf]]" "diagram.pdf"
        // cursor is inside "diagram" part: col 2
        let result = Refactor.rename true folder doc (Position.Mk(0, 2)) "newdiagram"

        Assert.Equal(Some("diagram.pdf", "newdiagram.pdf"), attachmentRenameChange result)

    [<Fact>]
    let extensionPreservedWhenUserProvidesFull () =
        // User provides "newdiagram.pdf" (same extension) → should not double-up
        let doc, folder = mkFolderWithAttachment "[[diagram.pdf]]" "diagram.pdf"
        let result = Refactor.rename true folder doc (Position.Mk(0, 2)) "newdiagram.pdf"

        Assert.Equal(Some("diagram.pdf", "newdiagram.pdf"), attachmentRenameChange result)

    // ─── Link text updates ────────────────────────────────────────────────────

    [<Fact>]
    let singleReferenceIsUpdated () =
        // One [[diagram.pdf]] link → link text should change to "newdiagram.pdf"
        let doc, folder = mkFolderWithAttachment "[[diagram.pdf]]" "diagram.pdf"
        let result = Refactor.rename true folder doc (Position.Mk(0, 2)) "newdiagram"

        let linkEdits = attachmentLinkEdits "doc.md" result
        Assert.Equal(1, linkEdits.Length)
        let _, newText = linkEdits[0]
        Assert.Equal("newdiagram.pdf", newText)

    [<Fact>]
    let multipleReferencesAreAllUpdated () =
        // Two links in the same doc referencing the same attachment
        let content = "[[image.png]] and [[image.png]]"
        let doc, folder = mkFolderWithAttachment content "image.png"
        let result = Refactor.rename true folder doc (Position.Mk(0, 2)) "photo"

        let linkEdits = attachmentLinkEdits "doc.md" result
        Assert.Equal(2, linkEdits.Length)

        for _, newText in linkEdits do
            Assert.Equal("photo.png", newText)

    // ─── Skip conditions ──────────────────────────────────────────────────────

    [<Fact>]
    let skipWhenCursorOutsideLink () =
        // Cursor is at col 0 which is on the '[' bracket — outside the link content range
        let doc, folder = mkFolderWithAttachment "[[diagram.pdf]]" "diagram.pdf"
        let result = Refactor.rename true folder doc (Position.Mk(0, 0)) "newdiagram"

        Assert.Equal(Refactor.Skip, result)

    [<Fact>]
    let skipWhenAttachmentNotRegistered () =
        // Attachment file is NOT in folder.attachments → should fall through to Skip
        let doc = Helpers.FakeDoc.Mk("[[diagram.pdf]]", path = "doc.md")
        let folder = Helpers.FakeFolder.Mk([ doc ]) // no withAttachment call
        let result = Refactor.rename true folder doc (Position.Mk(0, 2)) "newdiagram"

        Assert.Equal(Refactor.Skip, result)
