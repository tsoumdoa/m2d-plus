# On-Mac test checklist

For the first run of the mac package on a real Mac ([design](design/macos.md)). Record the Mac model, chip,
RAM, macOS version, Rhino build (`Rhino ▸ About Rhinoceros`) and whether Rhino runs .NET 8 or later
(`SetDotNetRuntime`, if present). Mark each step pass/fail with a note; attach the self-test report.
Allow about 45 minutes.

## 0. Before installing

- [ ] Rhino 8 for Mac **8.20 or later**, Grasshopper opens, no older M2D+ installed
      (Package Manager shows none; nothing M2D+ in `~/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper (…)/Libraries`).
- [ ] Copy `m2d-plus-<version>-rh8_20-mac.yak` and check its SHA-256 (`shasum -a 256 <file>`) against the
      `.sha256` file next to it.

## 1. Install and load

- [ ] Drag the `.yak` onto a Rhino viewport, accept, quit Rhino fully (⌘Q) and reopen.
- [ ] Package Manager lists `m2d-plus` for this version (distribution `rh8_20-mac`).
- [ ] Open Grasshopper. No load error dialog; nothing about M2D+ in `Grasshopper ▸ … ▸ Developer` load messages.
- [ ] **Params ▸ Util ▸ M2D+ Drawing** is there with the M2D+ icon (no M2D+ tab). The icon is drawn, not blank.

## 2. Self-test (automated part)

- [ ] Place an M2D+ Drawing, right-click it: the menu has **no Parallel views** item and ends with
      **Platform self-test…**.
- [ ] Run **Platform self-test…**. Rhino is busy for a few seconds, then a report dialog opens.
      Overall should be **PASS**. Every line must be `[pass]`; note each `[FAIL]` exactly.
- [ ] The report was saved to `~/Documents/M2DPlus-selftest-<time>.txt`; attach it.
- [ ] Compare the memory probe with Terminal: `sysctl hw.memsize` equals "physical"; from `vm_stat`,
      (Pages free + Pages speculative + Pages inactive) × page size is close to "available" (`vm_stat` subtracts
      speculative pages from "Pages free"; the value moves, so expect the same order of magnitude).
- [ ] "fixture vs Windows baseline" says `match` for plan, elevation, axonometric and section. If not, keep
      the exact failure text: that is the Windows-vs-Mac HLD difference to study.
- [ ] Note the fixture drawing time and the cancellation line (whether it cancelled inside native HLD or
      between views).
- [ ] Clipboard (manual; the self-test leaves the clipboard alone): **Copy report** in the dialog puts the
      report on the clipboard (paste in TextEdit).
- [ ] While another M2D+ component is drawing, **Platform self-test…** only adds a remark asking to wait.

## 3. Drawing (manual)

Use a new document in mm with a few solids (box, cylinder, sphere) and a mesh, plus a clipping plane.

- [ ] Wire `Top` into V: the drawing appears asynchronously; the status footer and progress bar animate
      while running and settle when done; Visible/Hidden outputs and the Rhino preview are right.
- [ ] `Front`, `SW`, a Plane and a GH Box (section): cut regions, hatches and Beyond look right.
- [ ] Wire a list of **four** views: they draw one after another (status counts views; no "workers");
      every Info contains `Route: in Rhino's process`.
- [ ] Info output and **Copy info**/**Copy settings as text** paste correctly.
- [ ] **Cancel** (button and Esc) during a slow set: the status says cancelled at once, nothing stale is
      published; the next Run works (it may wait for the running native call).
- [ ] Change geometry while running: the stale result is never shown; the new one is.
- [ ] Right-click **Tolerance ▸ Custom…** and **Min visible**/**Beyond** custom entries: the inline text box
      appears, accepts Enter, rejects bad text with the red message.
- [ ] **Create view from current viewport**, **Convert to view boxes**, **Flip view direction**, **Select view
      objects**; the **View box depth** dialog (Eto) opens, looks right and returns values.
- [ ] Target and Source inputs place the drawing; **Bake drawing** and **Bake one** create layers, curves and
      hatches; undo works.
- [ ] `visibility = pipes` on a pipe model: draws or falls back to HLD with a remark.
- [ ] Save the `.gh`, close and reopen it: settings and menu state restore; reopen in Windows if possible.
- [ ] Canvas: the component draws correctly at several zoom levels, in light and dark GH canvas themes;
      tooltips over the footer show the stage detail.

## 4. Memory and admission

- [ ] Open a larger model (e.g. the 305-object building fixture) and draw a set of views. Watch Activity
      Monitor's Rhino memory: it should stay below about 35% of RAM plus Rhino's own use.
- [ ] Force a refusal: wire a very heavy scene or many views; the component shows a red runtime error
      "Too large to draw inside Rhino on this platform…" with the estimate and budget (Info stays empty), and
      Rhino stays responsive. Record the scene size where refusals begin.

## 5. Comparison with Windows (optional, recommended)

- [ ] Open the same `.gh`/`.3dm` on Windows with the win package and on the Mac; compare Visible/Hidden counts
      and lengths in Info for each view. Differences beyond a few segments are worth an issue.

## 6. Probe for a future Mac worker (do not install anything)

These decide whether the second-instance worker in the design can work.

- [ ] With Rhino open, run in Terminal: `open -n -a "Rhino 8" --args -nosplash`. Does a second Rhino start,
      and is it licensed (no licence prompt or "in use" dialog)? Quit it afterwards.
- [ ] `open -n -a "Rhino 8" --args -nosplash -runscript="_-Exit"`: does the second instance start and exit
      by itself? Time it.
- [ ] Is a `rhinocode` command available? Find it with `mdfind -name rhinocode` or inside `Rhino 8.app`.
      After `StartScriptServer` in Rhino, does `rhinocode list` show the instance?
- [ ] `echo $TMPDIR | wc -c` (Unix socket paths are limited to 104 bytes).

## Report back

The saved self-test report, this checklist with notes, Rhino/macOS versions, and screenshots of anything
wrong (menus, icons, canvas, dialogs).
