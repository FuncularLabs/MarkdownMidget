# Parked ideas — not planned; open an issue to revive one

Nothing on this page is scheduled. Each entry is picked up only if a user asks for
it: [open an issue](https://github.com/FuncularLabs/MarkdownMidget/issues) to revive
one. What is planned lives in [ROADMAP.md](../ROADMAP.md); what shipped lives in
[CHANGELOG.md](../CHANGELOG.md).

Two kinds of thing, kept apart: **Later** is ideas that were wanted but are not
planned; **Won't unless asked** is limits the app has on purpose.

---

## Later

Sized and ordered in [plans/queued-features.md](plans/queued-features.md), which
also explains how the work should land: one user-visible feature per release, with
infrastructure riding along with whatever needs it. It was written when these were
queued, so it also sizes items that have since shipped.

- **Spell check follow-ups** (the 0.5.0 stack shipped en-US only, app-private
  dictionary): language selection. Sharing the OS dictionary was considered and
  deliberately rejected as too risky. (The one-way "import words from Word's
  CUSTOM.DIC" listed with it shipped in 0.8.0.) Picked up only if a user asks.
- **.NET 8 build + portable self-contained build.** The multi-target plan (net8 /
  net10 / portable ~63 MB) is scoped and the code already compiles for net8; just
  needs the csproj multi-target + extra publish profiles + release-workflow matrix.
  Picked up only if a user asks.
- **Editor bundle lazy-load.** Mermaid pulled the bundle from ~560 KB to ~3.9 MB
  (exe 2.9 → 6.4 MB). Code-split Mermaid so it loads only when a `mermaid` block
  is present — switches esbuild to ESM chunks + adapts the HTML/extraction.
  Picked up only if a user asks.

---

## Won't unless asked (known limits, parked deliberately)

Filled from the 2026-09-10 audit. These are stated, not hidden; each is a choice.
The user-facing statement of them is HELP's **Known limits** section, which says
what each one costs a reader and what to do instead; this list is the decision.

- **Spell check is en-US only, and the UI is English only.** Language selection
  is under Later; localisation is not planned.
- **Task-list checkboxes render but cannot be inserted from the UI.** The GFM
  preset supports `- [ ]`; there is no menu or toolbar item for it. Cheap to add
  if asked.
- **The source view is Consolas 14, no font, size or zoom control.** Zoom is the
  formatted view's. Now that the source view is coloured this will be asked for.
- **PDF is the only export.** No HTML or Word export; the markdown file *is* the
  portable form.
- **One document per window, no tabs, no Window menu.** Deliberate SDI; see the
  cross-instance registry under Someday in
  [ROADMAP.md](../ROADMAP.md#make-multiple-instances-behave-like-one-application).
- **A document that passes through the formatted view is saved in the app's
  markdown conventions.** Pinned and documented since #2: reference-style links
  become inline, setext headings become ATX, indented code becomes fenced, an
  underscore in an image's alt text is escaped — and emphasis that opens or
  closes on punctuation touching an emoji keeps its text but may lose its marks.
  (Tight lists no longer loosen; that one was fixed rather than parked.) A
  document opened and saved entirely in the source view is written back as typed:
  since #2 the source view opens on the file's own text rather than the editor's
  re-serialisation of it, so Ctrl+E before the formatted view changes anything is
  the way round this limit. Preserving each document's own style *through the
  formatted view* would be a second serialiser to test and is not planned.
- **Mermaid ships in the bundle** whether or not a document uses it (lazy-load is
  under Later).
