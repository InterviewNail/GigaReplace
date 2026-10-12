# GigaReplace

A Windows desktop app (WPF, .NET 10) that renames things across a .NET solution: it finds and
replaces text in file contents, file names and folder names. Before changing anything, it
analyzes each match. When a change could break the build, orphan stored data or change the
app's identity, it asks you what to do.

## Run it

```powershell
dotnet run --project src/GigaReplace
```

Requires the .NET 10 SDK on Windows. Tests: `dotnet test` (the end-to-end test creates a
solution with `dotnet new`, renames a copy and builds it; skip it with
`--filter "Category!=Integration"`).

## How a run works

1. **Setup.** Pick a folder or a `.sln`/`.slnx`, then add find → replace pairs. Each pair can be
   **Strict** (match case exactly) or not. When it isn't, **Mirror** carries the match's
   casing onto the replacement: `OrderHub → SalesDesk` also turns `orderHub` into `salesDesk`
   and `ORDERHUB` into `SALESDESK`. Pairs run top to bottom, and text an earlier pair matched
   isn't matched again.
2. **Scan.** Scanning only reads. It indexes the whole tree first (what the C# declares, which
   types are serialized, projects, identity GUIDs, file paths written in project files), then
   judges every match. Plain renames are approved automatically; risky ones are flagged.
3. **Decide.** Choose one of two ways:
   - **Review screen first:** a list of every change with filters, details and reasons.
     Flagged items need a decision before Apply is enabled. *Step through* shows them one at
     a time.
   - **Prompt during the run:** a dialog appears for each flagged change as the run reaches
     it, then the run offers to apply.

   Each decision can cover one match, **every occurrence of the same code name** (which keeps
   declarations, references and doc comments in step), or **all matches like this one**.
   You can also type custom replacement text.
4. **Checks before writing.** Whatever you switched off, every plan is checked for:
   - names renamed in some places but not others (C#, XAML, MSBuild properties, `#if` symbols)
   - file and folder names that would clash
   - paths in `.csproj`, `.sln`, `.slnx`, XAML, `.resx` and WiX files that would stop resolving
   - characters an 8-bit file can't store

   Problems come with one-click fixes.
5. **Apply.** Either:
   - **Copy to a new folder:** the source is never touched. Skipped and git-ignored files
     aren't copied, and `.git` is optional.
   - **In place:** every step is written to an undo journal first, with a backup of each
     original file. The journal lives in `%LOCALAPPDATA%\GigaReplace\Journals`. The Results
     tab can undo a run, even one that was interrupted. If a folder can't be renamed because
     another program is using it (an IDE's build process, or a terminal whose current folder
     is inside it), its contents move into the renamed folder instead. The old folder is left
     behind empty, and the Results tab says so.
6. **Build check.** *Build it with dotnet* runs `dotnet build` on the result and lists any errors.

## What gets flagged

Each kind can be switched off in Setup.

| Kind | Examples |
| --- | --- |
| Build breakers | A match inside another word (“nail” in `Thumbnail`); a new name that clashes with an existing type or member, or hides one; names not declared in the tree (so they come from .NET or NuGet); invalid names and C# keywords; unusual casing; renamed NuGet package IDs |
| Persisted or external data | Properties of serialized types (JSON, XML, EF, `IOptions`, user settings) unless their stored name is fixed by an attribute; strings used as file or folder names, registry keys, mutex and pipe names, environment variables, configuration and resource keys, URLs, connection strings, reflection names |
| App identity | `AssemblyName`, `RootNamespace`, `PackageId`, `InternalsVisibleTo`, manifests, installer scripts, and project files whose default assembly name or namespace would change |
| Watch-list | Any match on a line containing your own terms (one per line; `/regex/` allowed) |
| PDF documents | PDF text that has to be squeezed or stretched by more than 10% to fit; PDF text that can only be edited by hand (see below) |

Optionally, **new identity GUIDs** (UpgradeCode, ProductCode, ProjectGuid, UserSecretsId,
assembly GUID) are generated and applied consistently everywhere they appear, so a renamed
fork can be installed next to the original.

## PDF files

PDFs are searched too (switch it off in Setup). Matches in the document properties (title,
author, subject, keywords and the XMP metadata) are changed like any other text. Text on the
pages is changed only when that can be done without damaging the page:

- The new text is drawn with the PDF's own font, so every letter it needs must already be in
  that font. Most PDFs embed only the letters they use, so `OrderHub → SalesDesk` may fail for
  lack of an "S". The match is then listed as **Edit by hand** and never changed.
- The new text is fitted to the old text's width (horizontal scaling), so nothing after it on
  the line moves. A fit beyond ±10% is flagged so you can check how it looks.
- Text the PDF draws in separately placed pieces, inside part of a ligature, in vertical or
  Type 3 fonts, or on pages whose fonts can't be decoded is listed for you to edit by hand.
  So is everything in encrypted or digitally signed PDFs.

Changes are written as an *incremental update*: the original bytes stay as they are and the
changed objects are appended, as PDF editors do. Annotations, form fields and bookmarks aren't
searched. If the PDF is generated from files in the tree (Markdown, Word, report templates),
changing those and regenerating it gives the cleanest result.

## Good to know

- Files keep their encoding, byte order mark and line endings byte for byte. A file counts as
  text only if decoding and re-encoding it reproduces the original bytes. Binary files other
  than PDFs are never edited, but can be renamed.
- `.git` folders are never changed. `bin`, `obj`, `.vs` and friends are skipped by default, and
  `.gitignore` files in the tree are honored (global git excludes aren't read).
- C# is analyzed with the Roslyn parser, without compiling. The analysis is thorough but
  still heuristic, so run the build check afterwards. VB, F#, Razor and other files are
  treated as text; their words only stop "not declared here" flags.
- Settings can be saved as profiles (`*.grprofile.json`). The last one used is restored at
  startup.

## Layout

| Path | What's there |
| --- | --- |
| `src/GigaReplace.Core` | The engine: matching and casing (`Text`), tree walking and `.gitignore` (`FileSystem`), per-file analyzers (`Analysis`), PDF reading and rewriting (`Pdf`, on top of PdfPig), scanning and plan validation (`Planning`), copy, in-place and undo (`Apply`), profiles and the CSV report |
| `src/GigaReplace` | The WPF app (MVVM with CommunityToolkit.Mvvm, Fluent theme that follows the system light/dark setting) |
| `tests/GigaReplace.Core.Tests` | xUnit tests for the engine |
