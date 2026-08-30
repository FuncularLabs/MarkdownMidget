# Design plan: Secure Markdown (password-encrypted documents)

Status: **design, awaiting decisions on the three RECOMMENDED points below.** No code
written. This document is the thing to agree before implementation, because the file
format is expensive to change once a user has encrypted files with it.

Reference implementation studied: **Markdown Monster 3.8** (its own binaries decompiled
this session). MM already ships password-protected files, and its choices are recorded
below as much for what to *improve* as to copy.

---

## 1. Threat model — what this protects, and what it does not

**Protects.** A `.mdenc` file at rest — on disk, in a backup, in a cloud-sync folder, on
a lost laptop — reveals nothing about its contents without the password. Someone who
copies the file, or tampers with its bytes, cannot read it or silently alter the
plaintext.

**Does NOT protect** (state these to the user, do not let the UI imply otherwise):

- **The decrypted document while it is open.** It lives in memory as plaintext; anything
  that can read this process's memory (a debugger, a memory dump, another admin process)
  sees it. This is inherent to editing.
- **The password against a keylogger or shoulder-surfer.** Out of scope.
- **Forensic recovery of the *old plaintext bytes* after File ▸ Encrypt.** Deleting the
  original `.md` removes the *accessible* copy — Explorer, search, "Open with" no longer
  see it — but on an SSD with wear-levelling the freed blocks are not truly erased and a
  forensic tool may recover them. The user's own words are the honest promise: "leaves no
  **accessible** copy on disk." We will not claim more, because a false security promise
  is worse than none. (A best-effort overwrite-before-delete is offered as an option in
  §6; it helps on HDDs and is near-useless on SSDs — documented as such.)

**Trust boundary.** The password never touches disk, never rides a relaunch argument,
never enters a log or a crash dump we write, and is held only in a `SecureString` for the
life of the window.

---

## 2. The file format

MM stores `__ENCRYPTED__` + a base64 blob inside a **normal `.md` file**. We do not: a
plaintext marker in a `.md` file means every tool treats it as markdown, the encryption
is invisible to the user, and there is no room for versioning or authenticated headers.
We use a **dedicated extension and a binary container**.

### Extension `[RECOMMENDED: .mdenc]`

Checked against collisions: `.md`/`.markdown` (ours), `.mdx` (JSX-markdown, taken),
`.mde` (compiled MS Access database, taken), `.smd` (Valve/SourcePawn model, taken).
**`.mdenc`** is clean, self-describing ("markdown, encrypted"), and unclaimed by known
tooling. Alternatives if you prefer: `.securemd`, `.mdcrypt`. **Decision 1 below.**

### Container layout (binary, little-endian)

```
offset  size  field
0       6     magic         "MDMSEC"  (identifies the format on sight)
6       1     format_ver    0x01
7       1     kdf_id        1 = PBKDF2-SHA256, 2 = Argon2id
8       4     kdf_param_a   PBKDF2: iteration count; Argon2: memory KiB
12      4     kdf_param_b   PBKDF2: 0; Argon2: (time_cost<<16 | parallelism)
16      16    salt          random, fresh EVERY save
32      12    nonce         random, fresh EVERY save (AES-GCM IV)
44      N     ciphertext    AES-256-GCM(plaintext)
44+N    16    tag           GCM authentication tag
```

- **Header = bytes [0, 44)** is passed to GCM as **Associated Authenticated Data (AAD)**.
  This binds the version, KDF id, KDF parameters, salt and nonce into the authentication:
  an attacker cannot downgrade the iteration count, swap the salt, or roll the format
  version back without the tag failing. Tamper **fails closed** — a modified file does not
  decrypt, it errors.
- **Fresh salt AND fresh nonce on every save.** This re-derives the key each save, so
  GCM's catastrophic nonce-reuse-under-one-key condition cannot arise even in principle,
  and it costs one KDF run per save (sub-second, and saves are user-initiated).
- **Versioned from byte 6**, so format v2 can add fields (e.g. a different AEAD) and v1
  files still open.

### Crypto choices

- **AEAD: AES-256-GCM.** In-box (`System.Security.Cryptography.AesGcm`), authenticated,
  standard. The auth tag is the integrity guarantee that lets us skip OS file locks (§7).
- **KDF: `[RECOMMENDED: Argon2id]`.** Password-based encryption in 2026 should resist
  GPU/ASIC cracking, which PBKDF2 does poorly. Argon2id (memory-hard) is the current
  best practice (OWASP, RFC 9106). Cost: it is **not in-box** — needs a small vetted
  package (`Konscious.Security.Cryptography.Argon2`, or `Geralt`/`NSec` over libsodium).
  The in-box alternative is **PBKDF2-SHA256 at ≥600,000 iterations** (OWASP floor), which
  needs no dependency and is *acceptable*, just weaker against dedicated cracking rigs.
  Because `kdf_id` is in the header, we can ship PBKDF2 now and add Argon2id in format v1
  without a breaking change — but the *default for new files* is the decision. **Decision
  2 below.**
- **Password → key** only through the KDF with the file's own salt. No machine key, no
  fixed IV (MM's `EncryptString` historically used a derived-but-static IV; we do not).

---

## 3. The editing experience is unchanged

Once decrypted, the document is ordinary markdown in the editor. Everything — themes,
spell check, source view, print, find — works identically. The *only* differences a user
sees:

- On **Open** of a `.mdenc` file, a password prompt appears before the content loads.
- The title bar carries a small **🔒 lock indicator** and `[Encrypted]`, the way
  `[Read Only]` already works.
- **Save** re-encrypts silently with the session password — no re-prompt.

The decrypted text exists **only in memory** (`CurrentText`, the editor buffer). It is
never written anywhere in the clear except where the user explicitly decrypts (§4).

---

## 4. Operations

Menu shape (rephrased from the brief for clarity; all under **File**):

| Item | Enabled when | Does |
|---|---|---|
| **Encrypt Document…** | current doc is *not* encrypted | Prompts to set a password (twice + strength hint + the no-recovery warning), then transactionally replaces the on-disk file with a `.mdenc` and removes the old plaintext. In-memory content unchanged. |
| **Change Password…** | current doc *is* encrypted | Prompts for a new password; next save re-keys. |
| **Convert to Unencrypted…** | current doc *is* encrypted | Big warning, then writes a plaintext `.md` and removes the `.mdenc`. |
| **Save As…** | always | The type dropdown offers *Markdown (\*.md)* and *Secure Markdown (\*.mdenc)*; choosing the latter for a plaintext doc prompts for a password. This is the second path to encryption, equivalent to Encrypt Document. |

Open path: opening a `.mdenc` (via Open dialog, double-click, drag-drop, recent files,
command line — **all** entry points, per the guard-coverage rule) routes through a
password prompt. Wrong password → the GCM tag fails → "Incorrect password, or the file is
damaged" (the two are cryptographically indistinguishable, which is correct — we do not
leak which).

Session key lifetime: the `SecureString` password is held while the window is open,
cleared on close. **Never** persisted, **never** passed to a relaunched process.

---

## 5. Opt-in Open filter

Default Open filter stays `*.md`. A setting (**Edit ▸ Settings**, an unobtrusive
checkbox) adds `*.mdenc` to the Open dialog's Markdown filter. `.mdenc` files are always
openable by typing the name or via "All files"; the setting only controls whether they
show by default. Matches the brief.

---

## 6. Data-loss safety (transactional writes)

Every write of a `.mdenc` — Save, Encrypt, and the encrypted backup — uses the pattern the
`BackupStore` and settings writer already use here, hardened:

1. Serialize the container into a byte array in memory.
2. Write to a temp file **beside the target** (same volume, so the rename is atomic):
   `target.<pid>.tmp`.
3. **`FileStream.Flush(flushToDisk: true)`** on the temp (the fsync — so a power cut after
   the rename cannot leave an empty file).
4. **Read the temp back and decrypt it with the in-memory key**, comparing the plaintext
   to what we meant to save. This is MM's write-then-read-back loop, made stronger: MM
   compares ciphertext strings; we prove the bytes actually *decrypt to the right
   plaintext* before trusting them. If it does not verify, abort and keep the original —
   **never** delete the good file for a bad write.
5. `File.Move(temp, target, overwrite: true)` — atomic replace on the same volume.
6. Clean up the temp in a `finally`.

The invariant: **at no instant is the only copy of the user's data a partial or
unverified file.** A crash at any step leaves either the intact original or an intact new
file, never a truncated one.

**Encrypt Document (in-place)** is the same pattern plus the plaintext removal, ordered so
the encrypted file is fully written and verified *before* the plaintext `.md` is deleted.
Optional best-effort overwrite of the plaintext before delete (documented as effective on
HDDs, near-useless on SSDs — no false promise).

---

## 7. Leakage safety — the hard parts

### 7a. Crash-recovery backup (the crux)

The 0.6.3 crash store writes **plaintext** markdown to
`%LocalAppData%\MarkdownMidget\backup\{id}.md` every few seconds while a doc is dirty. For
an encrypted document that is a continuous plaintext leak of exactly the sensitive
content — the worst possible interaction, and the one this design most has to get right.

**Decision: the backup snapshot of an encrypted document is itself encrypted**, with the
same session key, as a `.mdenc`-format blob (`{id}.mdenc.bak`). Rationale:

- Keeps crash protection for the files that most need it.
- No plaintext of a secured document ever hits disk.
- On recovery, the existing "unsaved work found" flow gains a password prompt: the snapshot
  decrypts with the password, or is discarded if the user cannot supply it.

Subtleties the implementation must honour:

- The snapshot is only encrypted **once the document is encrypted**. A brand-new untitled
  doc the user is still typing is plaintext-backed as today — it is not yet sensitive, and
  the moment they Encrypt, the next snapshot is encrypted and **the prior plaintext
  snapshot is securely discarded**.
- The recovery snapshot stores the salt/nonce like any container; the password is the only
  thing not on disk.
- If the app cannot hold the key (it was never entered — e.g. recovering after a crash
  that happened before the user re-entered the password on this launch), the snapshot
  simply prompts, exactly like opening the file does.
- **Replay lens:** a crash between "write encrypted snapshot" and "delete old plaintext
  snapshot" must not leave a plaintext snapshot behind. Order: write+verify the encrypted
  snapshot, *then* delete the plaintext one — same ordering discipline as §6.

### 7b. No plaintext temp files

The transactional write (§6) writes an **encrypted** temp (`.mdenc.tmp`), never a
plaintext one. There is no code path that writes the decrypted content to disk except the
explicit Convert-to-Unencrypted.

### 7c. Reopen-after-update re-prompts for the password

The 0.8.x update/restart feature relaunches carrying the document path. It must **never**
carry the password (it is a `SecureString`, not serialisable to an argument, and this is a
hard rule in the code + a test). So a relaunched window opening a `.mdenc` prompts fresh —
which is exactly the desired security property, and falls out for free as long as the
password stays off the command line and off disk.

### 7d. OS file locks vs. the auth tag

The brief floats "maybe exclusive locks on encrypted files." **Recommendation: no OS
lock.** Reasons: (1) an exclusive lock fights the atomic temp-rename in §6; (2) our app is
one-process-per-window, and a lock would block a second window or the update-restart from
reopening the same file; (3) the GCM auth tag already gives a **stronger** guarantee than
an advisory lock — external tampering cannot silently alter the plaintext, it makes the
file fail to decrypt. External *replacement* of the whole file is caught by the existing
external-change detector. So integrity comes from cryptography, not from a lock that costs
us concurrency. (Stated as a decision so it is not quietly reversed later.)

### 7e. In-memory hygiene

`SecureString` for the password. The decrypted markdown is an ordinary `string` and .NET
strings are immutable and GC-managed, so we cannot reliably zero them — this is a known,
unavoidable limitation of managed editors (MM has it too), and we state it rather than
pretend otherwise. We do avoid *unnecessary* copies (no plaintext in logs, no plaintext in
the crash-recovery path, no plaintext temp).

---

## 8. User warnings (exact, non-negotiable)

- **Setting a password** (Encrypt / Save As encrypted / Change Password): a modal that
  states, in plain words, *"There is no way to recover this document if you forget the
  password. Not by us, not by anyone. Write it down somewhere safe."* — with a typed
  confirmation of the password (enter twice) and a strength indicator.
- **Encrypt Document** additionally: *"The unencrypted copy will be removed from disk."*
- **Convert to Unencrypted**: *"This writes a readable copy with no password. Anyone with
  the file can read it."*

---

## 9. Testing — rigor against data-loss AND leakage

This feature earns its own hostile pass. The test matrix, at minimum:

**Round-trip & correctness**
- encrypt → decrypt → identical bytes, across ASCII, Unicode, emoji, empty, 10 MB.
- fresh salt+nonce every save (two saves of identical content ⇒ different ciphertext).
- header AAD binding: flip one KDF-param byte ⇒ decrypt fails (does not silently use
  tampered params).

**Fail-closed**
- wrong password ⇒ clean error, no partial content surfaced.
- truncated file / flipped ciphertext byte / flipped tag byte ⇒ fails, original never
  touched.
- a plaintext `.md` renamed to `.mdenc` ⇒ recognised as not-a-container (magic check),
  clean error.

**Data-loss (transactional)**
- kill between temp-write and rename ⇒ original intact (simulated via the seam).
- read-back-verify catches a corrupted temp ⇒ original kept, error surfaced.
- Encrypt aborts before deleting plaintext if the encrypted write fails verification.

**Leakage (the assertions that matter most)**
- after Encrypt, **grep the backup dir and temp dir for the known plaintext sentinel ⇒
  zero hits** (this is the test that would have caught MM's design).
- an encrypted doc's crash snapshot on disk does not contain the plaintext sentinel.
- the password never appears in: relaunch args, settings.json, crash.log, any temp.
- recovery of an encrypted snapshot prompts and round-trips.

**Entry-point coverage** (guard-coverage rule): Open dialog, double-click/file-assoc,
drag-drop, recent files, command-line arg — every one routes a `.mdenc` through the
password prompt.

The crypto core (`SecureMarkdownFormat` — serialize/parse/encrypt/decrypt over a
byte[]/SecureString seam) is pure and unit-testable without any UI, the way `CssValidator`
and `CustomDicImport` are.

---

# Part B — Deeply resolving the file-dialog crash

## The evidence points at a shell-extension access violation, not our code

From decompiling Markdown Monster 3.8 this session: **MM opens files with the exact same
`Microsoft.Win32.OpenFileDialog` we do** (`MarkdownMonster.dll`, `Button_Handler`). So the
dialog *class* is not the delta between MM and a bare WPF test app. Combined with the
history you gave — MM crashed **in NTDLL** until you removed shell extensions, and a bare
WPF dialog with those same extensions did **not** crash — the signature is:

> A faulty third-party **shell extension** (a thumbnail provider, preview handler, or
> context-menu handler) that the Vista-era **common item dialog** loads when it enumerates
> a folder, faults inside its own native code; the stack unwinds through NTDLL.

### "Could we just use the standard WPF dialogs instead?" — measured, and no

This was worth checking properly rather than assuming, so both assemblies were decompiled
this session and read directly.

**`Microsoft.Win32.OpenFileDialog` *is* the standard WPF dialog.** `Microsoft.Win32` is
merely the namespace WPF puts it in (it lives in `PresentationFramework.dll`, shipped with
WPF). There is no separate, more-vanilla WPF file dialog to switch to — we are already
using the plain one.

More importantly, the underlying shell API is identical everywhere on modern Windows:

| Runtime | What the dialog actually creates | Loads shell extensions? |
|---|---|---|
| **.NET 10 WPF** (ours) | COM `IFileDialog`, CLSID `DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7` | Yes |
| **.NET Framework WPF** | Same COM `IFileDialog` — its legacy `OPENFILENAME` path is gated behind `UseVistaDialog => Environment.OSVersion.Version.Major >= 6`, so it only ran on **Windows XP** | Yes |
| **.NET 10 WinForms** | Same `IFileDialog` (its legacy path is likewise vestigial) | Yes |

Verified: .NET 10's `PresentationFramework.dll` contains **zero** references to
`OPENFILENAME`/`RunFileDialog` — the legacy path was deleted outright as XP-era dead code.
.NET Framework still contains it, but cannot reach it on any supported Windows.

So a "plain vanilla WPF app" on Windows 11 opens the **same COM object, loading the same
shell extensions, in-process**, as we do. Switching dialog APIs is a genuine no-op for this
crash. The remembered difference is real but must come from somewhere else — most likely
**which folder the dialog lands in** (shell handlers are instantiated for the items
actually shown, so a test app pointed at an empty folder never touches the faulting one)
and **what else is loaded in-process** (both MM and Markdown Midget host WebView2, a large
native/COM surface a hello-world app does not have).

Since no in-process dialog choice avoids loading third-party shell code, and an AV in that
code is uncatchable, **process isolation is the only mechanism that keeps the app alive.**

### The gap in out-of-process alone — and the library survey (explored 2026-08-28)

**The stated fear is correct:** out-of-process isolation stops the *app* from dying, but if
the shell dialog crashes in the child, that pick attempt still yields no file. Retrying
often works — shell-extension faults are typically triggered by specific folder contents or
the preview pane, and every child is a fresh process — but a handler that faults on the
dialog's initial folder fails deterministically, and that user still cannot Open/Save As.
Isolation alone converts "app dies" into "picker unavailable." Not good enough.

**Survey of alternative dialog libraries** (each verified, not assumed):

| Candidate | Verdict |
|---|---|
| **Ookii.Dialogs.Wpf** | Decompiled the actual NuGet package this session: its `VistaFileDialog` instantiates **the same COM `IFileDialog`** (`CreateFileDialog()` → `NativeFileOpenDialog`) with the same `IFileDialogEvents` plumbing WPF uses. Same shell extensions load in-process. **No help.** |
| **WindowsAPICodePack** (`CommonOpenFileDialog`) | Same `IFileDialog` CLSID wrapper. **No help.** |
| **chris84948/WPF-File-Dialog-Control** | Genuinely pure-WPF Explorer-style dialog — proof the approach works — but a small unmaintained hobby project; not a dependency to put in front of users' documents. **Prior art only.** |
| **Avalonia `ManagedFileChooser`** | The strongest prior art: a maintained, MIT-licensed, fully managed file dialog (quick-links rail + file list + filename/filter), shipped by a major framework precisely as the fallback for when native pickers can't be trusted/used. It is Avalonia-only — not usable from WPF — but it validates both feasibility and the fallback architecture. |

**Conclusion: no maintained drop-in managed file dialog exists for WPF.** Rolling our own
is entirely feasible (two independent proofs above), and it is the piece that closes the
gap isolation leaves.

### The composed architecture (supersedes "out-of-process alone")

Two layers, because neither suffices by itself:

1. **Native picker, isolated out-of-process.** Everyone with a healthy shell keeps the
   full Explorer dialog — search, Quick Access, OneDrive, thumbnails, familiarity. A
   faulting shell extension kills only the throwaway child.
2. **`MidgetFilePicker` — our own pure-WPF fallback dialog.** When the child dies, the
   app says one honest sentence ("Windows' file picker crashed — this is caused by an
   Explorer add-on, not Markdown Midget — using the built-in picker instead") and
   immediately shows ours. It **cannot** be crashed by shell extensions because it runs
   zero shell code: navigation is pure `System.IO`.

   Feature bar (matching the request: same navigation abilities as the standard dialog):
   - **Editable address bar** with breadcrumb segments (type a path, paste UNC, Enter).
   - **Left rail**: Desktop, Documents, Downloads, this app's recent folders, all drives.
   - **Folder tree view** (expand-on-demand, no eager recursion).
   - **File list view**: Name / Date modified / Size columns, sortable, keyboard
     navigation, type-ahead, double-click to open/enter.
   - Filename box + filter dropdown (same filter strings the native dialog gets).
   - Save mode adds: New Folder button, overwrite confirmation, extension enforcement.
   - Hidden/system files toggle (off by default, like Explorer).
   - **Deliberately absent**: thumbnails, preview pane, per-file shell icons (icon
     *handlers* are third-party code — the exact class that faults; we use static
     folder/file glyphs keyed by extension), shell context menus, Windows Search. These
     are the crash surface; excluding them is the point.
   - A Settings toggle: **"Always use the built-in file picker"** — and the app turns it
     ON automatically when it detects the out-of-process native dialog crashed (child
     exited without a result), telling the user it did so and where to turn it back off.
     A known-bad shell should not cost one failed dialog per session; one crash is the
     detection, and from then on the picker just works. (Decided 2026-08-29.) Manual
     opt-in remains for users who simply prefer it.
   - Longer-term direction (decided 2026-08-29, stubbed in ROADMAP.md): model the picker
     as a functional equivalent of Avalonia's MIT `ManagedFileChooser` for WPF, with the
     navigation core kept framework-agnostic so it could be broken out into its own OSS
     project and grown WinForms/other front ends later. Avalonia's feature decisions are
     the base for what ships in Midget now; context-menu/icon/thumbnail questions are
     deferred to the extraction, not blocking.

3. **One chokepoint.** All five dialog call sites (Open, Save As ×3, CUSTOM.DIC import)
   route through a single `FilePickerService` that owns the try-native→fallback strategy —
   the guard-coverage rule applied from day one, and the `.mdenc` filter logic lands in
   exactly one place.

**Answer to the fear, precisely:** with isolation alone the fear is true. With the
fallback picker composed on top, a shell-extension crash costs the user one explanatory
sentence and lands them in a fully navigable picker — Open/Save As/Save always complete.
And the fallback cannot be reached in a broken state, because the native dialog's failure
happens in a process whose death we observe cleanly.

## Why 0.8.2's crash handler cannot fix this one

0.8.2 catches **managed** exceptions on the dispatcher. A shell extension faulting in
native code raises an **`AccessViolationException`**, and since .NET Core removed
`legacyCorruptedStateExceptionsPolicy`, an AV is **uncatchable and process-fatal** by
design — no `try/catch`, no `DispatcherUnhandledException`, no amount of thread isolation
inside our process stops it. (A useful corollary: if Eric's `crash.log` from 0.8.2 is
**empty** for a crash that did happen, that near-proves it was an AV — the managed handler
never ran. That is itself a diagnostic.)

So "deeply resolve" genuinely requires getting the shell handlers **out of our process**.

## The deep fix: out-of-process file dialog `[RECOMMENDED — Decision 3]`

Run the file dialog in a **short-lived child process** — our own exe in a `--pick-file` /
`--pick-save` mode — that shows the dialog, prints the chosen path to stdout, and exits.

- The child loads the faulty shell extension; if it faults, **only the child dies**. The
  parent sees a non-zero exit with no path and shows: *"The file picker closed unexpectedly
  — this is almost always a faulty Explorer add-on (a preview or thumbnail handler), not
  Markdown Midget. See Help for how to find it."* The editor and the open document survive.
- The child is us, so no new binary, no dependency, trivial to sign (already signed).
- IPC is a single line of stdout (the path) — no shared state, no marshalling.
- Testable: the parent's "child exited without a path" branch is unit-coverable with a stub
  child.

This is the robust resolution: it converts an uncatchable whole-app crash into a contained,
explained, survivable event — the same philosophy as 0.8.2, but reaching the class 0.8.2
provably cannot.

**Lighter hardening to ship alongside** (cheap, reduces the trigger surface):
- Set `InitialDirectory` only to a folder confirmed to exist; never leave it dangling.
- Consider suppressing the places we pass `RestoreDirectory` where it is not needed.
- These reduce *how often* a bad handler is instantiated; the out-of-process isolation is
  what makes the remaining cases non-fatal.

**Confirming the root cause with Eric** (do this regardless): Event Viewer ▸ Windows Logs ▸
Application, at the crash time, will name the **faulting module** for an
`Application Error`/`.NET Runtime` entry. If it is a non-Microsoft DLL (a shell extension),
that confirms the diagnosis and even names the culprit add-on. His 0.8.2 crash.log being
empty for a real crash corroborates it.

---

## Decisions

1. **Extension — `.mdenc`. DECIDED 2026-08-26.**
2. **KDF default — Argon2id. DECIDED 2026-08-28** (user concurred). PBKDF2-SHA256 remains
   a registered `kdf_id` for potential future use; new files default to Argon2id.
3. **Crash fix — the composed architecture: out-of-process native picker + `MidgetFilePicker`
   managed fallback.** "Use standard WPF dialogs instead" was measured to be a no-op (we
   already use them; all runtimes converge on the same COM `IFileDialog`). Library survey
   found no maintained managed WPF dialog (Ookii/WindowsAPICodePack decompile to the same
   COM object; Avalonia's `ManagedFileChooser` is the validating prior art but is not WPF).
   Isolation alone was judged insufficient — it saves the app but not the pick — hence the
   fallback picker. **Proposed 2026-08-28, awaiting go-ahead.**

Everything else in this document is a recommendation I will implement as written unless you
redirect.
