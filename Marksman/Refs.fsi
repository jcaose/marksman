module Marksman.Refs

open Ionide.LanguageServerProtocol.Types

open Marksman.Config
open Marksman.Doc
open Marksman.Folder
open Marksman.Cst
open Marksman.Names
open Marksman.Paths

type InternNameNode = Node<InternName>

[<RequireQualifiedAccess>]
type FileLinkKind =
    | FilePath
    | FileName
    | FileStem
    | Title

module FileLinkKind =
    val detect: ComplWikiStyle -> DocId -> string -> (Doc -> FileLinkKind)

type FileLink = {
    link: string
    kind: FileLinkKind
    doc: Doc
}

module FileLink =
    val doc: FileLink -> Doc
    val filterMatchingDocs: Folder -> InternName -> seq<FileLink>
    val filterFuzzyMatchingDocs: Folder -> InternName -> seq<Doc>

type DocLink =
    | Explicit of FileLink
    | Implicit of Doc

module DocLink =
    val doc: DocLink -> Doc

[<RequireQualifiedAccess>]
type Dest =
    | Doc of FileLink
    | Attachment of RelPath * Folder
    | Heading of DocLink * Node<Heading>
    | LinkDef of Doc * Node<MdLinkDef>
    | Tag of Doc * Node<Tag>

module Dest =
    val tryDoc: Dest -> option<Doc>
    val doc: Dest -> Doc
    val absPath: Dest -> option<AbsPath>
    val range: Dest -> Range
    val scope: Dest -> Range
    val uri: Dest -> string
    val location: Dest -> Location

    val tryResolveSym: Folder -> seq<Folder> -> Doc -> Syms.Sym -> seq<Dest>
    val tryResolveElement: Folder -> seq<Folder> -> Doc -> Element -> seq<Dest>
    val findElementRefs: bool -> Folder -> seq<Folder> -> Doc -> Element -> seq<Doc * Element>
