# RFC: Attachment File Links

## Problem

Marksman checks `[[wiki-links]]` and flags any that don't resolve to a markdown document as
broken (error diagnostic). However, Obsidian-style vaults routinely link to non-markdown
files — images, PDFs, audio, video — using the same `[[filename.ext]]` syntax, and embed
them with `![[filename.ext]]`.

Currently:
- `[[Figure 1.png]]` is parsed as a cross-doc link, flagged as broken (false positive).
- `![[image.png]]` — the `!` prefix prevents Markdig from handing `[[...]]` to the wiki-link
  parser in current Markdig behavior, so no diagnostic is produced, but no semantic support
  (go-to-def, rename) exists either.
- No rename support for attachment files.

## Goal

Recognize non-markdown attachment files as valid link targets:

1. Suppress false-positive "broken link" diagnostics for `[[file.ext]]` when the file exists
   and has an accepted attachment extension.
2. Enable go-to-definition on attachment wiki links (opens the file in the OS/editor).
3. Enable LSP rename on attachment wiki links: renames the file and updates all referencing
   links.
4. Allow the list of attachment extensions to be configured per-project.

## Principles

| Behaviour                   | Description                                                 |
|-----------------------------|-------------------------------------------------------------|
| Diagnostics                 | No broken-link diagnostic when attachment file exists       |
| Broken link (missing file)  | Diagnostic still raised if the attachment does not exist    |
| Embed `![[...]]`            | No diagnostic; Markdig absorbs the `!` before `[[`         |
| Go-to-definition            | Resolves to the attachment file URI (zero range)           |
| Hover                       | Shows the relative path of the attachment                   |
| References                  | Not listed (attachments don't contain markdown symbols)     |
| Rename                      | Renames the file + updates all wiki links in the folder     |
| Completion                  | Attachments are NOT offered as completion candidates        |
| Code action (create file)   | Suppressed when target is an attachment extension           |

## Non-Goals

- Indexing attachment _content_ (OCR, PDF text extraction).
- Offering attachment files in completion.
- Transitive cross-folder attachment resolution (extra_folders, v2).
- Auto-detecting whether a `![[...]]` embed succeeded (requires Markdig changes).

## Configuration

Add to `.marksman.toml` under `[core]`:

```toml
[core]
# File extensions treated as attachment files.
# Wiki links to these extensions are not flagged as broken links.
# Default: Obsidian's full list (images + audio + video + PDF), plus common office/data/archive files.
attachment_file_extensions = [
  "png", "jpg", "jpeg", "gif", "bmp", "svg", "webp", "avif",
  "mp3", "wav", "ogg", "flac", "m4a", "3gp",
  "mp4", "mov", "mkv", "ogv", "webm",
  "pdf",
  "xls", "xlsx", "xlsm", "xltx", "xltm",
  "doc", "docx", "docm", "dotx", "dotm",
  "ppt", "pptx", "pptm", "potx", "potm", "ppsx", "ppsm",
  "zip", "7z", "tar", "gz", "bz2", "xz", "lz",
  "parquet", "json", "jsonl", "ndjson", "csv", "tsv"
]
```

Set to `[]` to disable attachment recognition entirely:

```toml
[core]
attachment_file_extensions = []
```

## Embed Syntax (`![[...]]`)

Obsidian uses `![[filename.ext]]` to embed attachments inline. With the current Markdig
pipeline, the `!` character triggers Markdig's image-link handling, which means `[[...]]` is
never handed to our `WikiLinkParser`. As a result:

- No wiki-link element is produced for `![[...]]` content — no diagnostic, no semantic features.
- This is correct behavior from a diagnostics standpoint (no false positives).
- If future work adds a dedicated embed-link parser, the `isEmbed` field on `WikiLink` (already
  added in the CST) will carry the flag, and the suppression logic in `Diag.fs` will activate.

## Implementation Notes

### New types and helpers

- `Config.coreAttachmentFileExtensions` — `option<array<string>>` field parsed from TOML.
- `Misc.isAttachmentFile` — mirrors `isMarkdownFile` but for attachment extensions.
- `MultiFile.attachments: Set<RelPath>` — set of relative paths for attachment files in the
  folder, populated at load time by `Folder.loadAttachments`.
- `Dest.Attachment of RelPath * Folder` — new case in the resolved-reference union.

### Resolution flow

`Dest.tryResolveSym` first tries the existing symbol-graph resolution (markdown docs, headings,
etc.). If that returns no results, it calls `tryResolveAsAttachment`, which calls
`Folder.tryFindAttachmentByInternName`. Attachment resolution requires exact `RelPath`
match; filename-only fallback is intentionally not used.

### Diagnostics

When `Dest.tryResolveSym` returns `Dest.Attachment`, `refs.Length = 1` in `checkLink`, and
no diagnostic is raised. When the attachment file does not exist, `refs.Length = 0` and a
broken-link diagnostic is raised as usual (unless suppressed by the embed-link rule).

### Rename

`Refactor.rename` now checks whether the cursor is on a wiki link that resolves to
`Dest.Attachment`. If so, it:
1. Emits a `RenameFile` document change (moves the physical file).
2. Emits `TextDocumentEdit` changes for every wiki link in the folder that references the old
   filename.

### Server file watching

`Server.mkServerCaps` registers a combined file-operation registration covering both markdown
and attachment extensions. `WorkspaceDidCreateFiles` / `WorkspaceDidDeleteFiles` detect
attachment files and call `Folder.withAttachment` / `withoutAttachment` to keep the index
up-to-date without a full reload.

## Interaction with `extra_folders`

This RFC targets the `main` branch which does not yet include the `extra_folders` feature.
When `extra_folders` is merged, `tryResolveAsAttachment` should be extended to search extra
folders as a fallback (mirroring how cross-folder doc resolution works).
