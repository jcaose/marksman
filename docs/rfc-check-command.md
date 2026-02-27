# RFC: `marksman check` — Standalone Lint Command

## Problem

Marksman currently operates exclusively as an LSP server. Obtaining diagnostics
(broken links, ambiguous links) outside of an editor session requires a wrapper
script that speaks the LSP protocol over stdin/stdout — a fragile, non-obvious
approach. This creates friction for:

- **CI pipelines** — no simple `marksman check` step, only complex LSP clients.
- **AI coding agents** — agents that fix broken links need a way to verify their
  work without running a full editor stack.
- **Pre-commit hooks** — hooks want a fast, exit-code-driven CLI tool.
- **Shell scripting** — `marksman check && …` should Just Work.

The planned feature was already listed in `docs/features.md`:

```
🗓 Add "check" command for standalone workspace checking.
```

## Goal

Add a `marksman check [PATH]` subcommand that:

1. Loads the workspace at `PATH` (defaults to `cwd`) exactly as the LSP server
   would — respecting `.marksman.toml`, `extra_folders`, the user config, and
   all configured markdown extensions.
2. Computes all diagnostics using the same engine as the LSP diagnostic path.
3. Prints them to stdout in a machine-readable, grep-friendly format.
4. Exits with code **0** if no diagnostics, **1** if any diagnostics are found.

## Non-Goals

- Fixing broken links automatically (that is a separate "build" command concern).
- Watching files for changes (`--watch` mode) — run in a loop externally if needed.
- Producing LSP-protocol JSON output (that is `marksman server`).
- Diagnosing files inside extra folders (same rule as the LSP session: extra-folder
  documents are not diagnosed from the primary session).

## Output Format

### Default: GNU error format

```
<file>:<line>:<col>: <severity>: <message> [MKS<code>]
```

Examples:

```
notes/index.md:3:1: error: broken link to document 'brett-scorza' [MKS002]
notes/index.md:7:5: warning: ambiguous link to document 'meeting' [MKS001]
notes/headings.md:2:3: warning: non-breaking whitespace in heading [MKS003]
```

Severity mapping:

| Entry type | Wiki link | Markdown link |
|---|---|---|
| `BrokenLink` | `error` | `warning` |
| `AmbiguousLink` | `error` | `warning` |
| `NonBreakableWhitespace` | `warning` | `warning` |

The GNU format (`file:line:col: severity: message`) is parsed automatically by
`make`, `flycheck`, `errorformat` in Vim/Neovim, and most CI log parsers.

Diagnostic codes match the LSP codes already emitted by the server:

| Code | Meaning |
|---|---|
| `MKS001` | Ambiguous link |
| `MKS002` | Broken link |
| `MKS003` | Non-breaking whitespace in heading |

### Optional: JSON format (`--format json`)

For AI agents and programmatic consumers, `--format json` emits a JSON array
of objects, one per diagnostic:

```json
[
  {
    "file": "notes/index.md",
    "line": 3,
    "col": 1,
    "severity": "error",
    "code": "MKS002",
    "message": "broken link to document 'brett-scorza'"
  }
]
```

If there are no diagnostics the array is `[]` and exit code is 0.

## CLI Interface

```
marksman check [OPTIONS] [PATH]

Arguments:
  [PATH]   Root directory of the workspace to check.
           Defaults to the current working directory.

Options:
  --format <text|json>   Output format (default: text)
  --verbose / -v         Increase log verbosity (same as `server`)
```

Exit codes:
- `0` — no diagnostics found
- `1` — one or more diagnostics found
- `2` — fatal error (path does not exist, config parse failure, etc.)

## Architecture

No new diagnostic logic is needed. The existing pipeline already separates
computation from LSP publishing:

```
Folder.tryLoad userConfig name folderId
  └─ Workspace.ofFolders userConfig [folder]   ← extra_folders loaded here
      └─ Diag.checkFolder folder extraFolders   ← reuse directly (no LSP types)
```

`Diag.checkFolder` returns `seq<DocId * list<Entry>>` where `Entry` is a plain
discriminated union (`BrokenLink`, `AmbiguousLink`, `NonBreakableWhitespace`).
The `diagToLsp` conversion is bypassed entirely — we format `Entry` values
directly into the chosen output format.

The text formatter needs:
- The relative file path (from `Doc.pathFromRoot doc`)
- The 1-based line/col from `Element.range` or the raw `Range`
- The severity (derived from element type, same logic as `diagToLsp`)
- The human-readable message (reuse `refToHuman`, `destToHuman` from `Diag.fs`)

### New module: `Marksman/Check.fs`

Contains the standalone check logic, independent of LSP types:

```fsharp
module Marksman.Check

type OutputFormat = Text | Json

/// Run diagnostics on the workspace rooted at `rootPath`.
/// Returns the total number of diagnostics found.
val check : rootPath: string -> format: OutputFormat -> verbosity: int -> int
```

Internally:

1. Load user config from `Config.userConfigFile`
2. Resolve `rootPath` to an absolute path; fail with exit 2 if it doesn't exist
3. Construct `FolderId` via `UriWith.mkRoot (AbsPath.toUri absPath)`
4. Call `Folder.tryLoad userConfig name folderId`; fail with exit 2 if None
5. Build workspace: `Workspace.ofFolders userConfig [folder]`
6. For each primary folder, call `Diag.checkFolder folder extraFolders`
7. Format and print each entry; count total diagnostics
8. Return count (caller exits 0 or 1)

### `Program.fs` changes

Add a `check` command alongside the existing `server` command:

```fsharp
let checkCommand =
    command "check" {
        description "Check workspace for broken links and other issues"
        inputs (pathArg, formatOpt, verbosity)
        setAction runCheck
    }

rootCommand args {
    …
    addCommand lspCommand
    addCommand checkCommand
}
```

## File Map

| File | Change |
|---|---|
| `Marksman/Check.fs` | New module: workspace loading, diagnostic formatting, entry point |
| `Marksman/Program.fs` | Add `check` subcommand; wire `Check.check` |
| `Marksman/Marksman.fsproj` | Add `Check.fs` to compile order (before `Program.fs`) |
| `docs/features.md` | Mark `check` as ✅ implemented; add usage example |
| `docs/rfc-check-command.md` | This document |

## Test Coverage

Tests live in `Tests/CheckTests.fs`:

| Test | What it verifies |
|---|---|
| `check_cleanWorkspace_exits0` | Workspace with no broken links → 0 diagnostics |
| `check_brokenWikiLink_reported` | Broken wiki link → 1 error, correct file/line/col |
| `check_brokenMarkdownLink_reported` | Broken markdown link → 1 warning |
| `check_ambiguousLink_reported` | Ambiguous link → 1 error |
| `check_extraFolderResolves_noDiag` | Link to extra-folder doc → no diagnostic |
| `check_jsonFormat_validJson` | `--format json` produces parseable JSON array |
| `check_noFiles_exits0` | Empty directory → 0 diagnostics, exit 0 |

## Usage Examples

**CI (GitHub Actions):**

```yaml
- name: Check markdown links
  run: marksman check
```

**Pre-commit hook (`.pre-commit-config.yaml`):**

```yaml
- repo: local
  hooks:
    - id: marksman-check
      name: marksman check
      entry: marksman check
      language: system
      types: [markdown]
      pass_filenames: false
```

**AI agent / shell script:**

```bash
# Check and capture output for the agent to parse
marksman check --format json | jq '.[] | select(.severity == "error")'
```

**Replacing `lint-links.py`:**

```bash
# Before
./lint-links.py

# After
marksman check
```
