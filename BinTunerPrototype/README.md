# BinTuner (prototype)

Standalone WinForms .NET 8 prototype for opening a Honda ECU `.bin` dump together
with a TunerPro-style `.xdf` definition file, browsing tables/scalars in a
TunerPro-like Parameter Tree, and editing table values with heatmap coloring,
undo/redo, and range operations (offset +/-, scale x/, percentage).

This is a separate, throwaway proof of concept — not part of the real ARTTUNER
program. The idea is to validate the .bin/.xdf editing workflow here first; the
parsing/read-write/edit-history code (everything outside `UI/`) has no WinForms
dependency and can be lifted into the real project later.

## Build

Requires the .NET 8 SDK. On Windows, `dotnet build` / `dotnet run` from the
`BinTuner/` folder works normally. On Linux/macOS you can still **build** (not
run — WinForms needs Windows) by installing the SDK via the official
`dotnet-install.sh` script and setting `EnableWindowsTargeting=true` (already
set in the `.csproj`).

```
cd BinTuner
dotnet build
```

## Current scope

- Open `.bin` (auto-backs up the original as `<file>.original` on load, never
  overwrites it)
- Open `.xdf` (TunerPro-compatible subset: `XDFHEADER`/`CATEGORY`, `XDFTABLE`
  2D/1D tables, `XDFCONSTANT` scalars, `EMBEDDEDDATA` address/size/sign/endian,
  linear `MATH` equations, static axis `LABEL`s or embedded-data axes)
- Parameter Tree (Scalars / Tables grouped by XDF category)
- Hex viewer with offset gutter, ASCII column, grayscale value shading, and the
  selected table's address range highlighted
- Editable table grid with heatmap coloring, multi-cell Offset +/-, Scale x/,
  and Percentage operations, clamped to the field's signed/unsigned byte range
- Undo/redo (50 steps)
- "Add table manually" dialog for when no `.xdf` exists yet — enter an offset
  you suspect is right and see it rendered as a table
- Save As only (never overwrites the source file); re-reads the saved file and
  reports the byte-level diff against the original as a sanity check

## Not implemented (by design)

- **No checksum calculation, no ECU flashing.** Files saved from this
  prototype are not valid to flash — that's Phase 3/4 of the full project and
  needs a sacrificial ECU to reverse-engineer and verify safely.
