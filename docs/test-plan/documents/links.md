# Link tests

For TEST-PLAN-1.0.md, areas LNK and WEB. 80 cases, then gestures G1–G7. Hosts are reserved names (`example.com`, `example.org`, `*.test`, `*.invalid`) or loopback and documentation addresses (`localhost`, `127.0.0.1`, `2001:db8::1`), so pressing **Open** by mistake does no harm. Work on a copy, and don't save it from the app: a save may rewrite its markdown.

## How to use

1. Open this file in the formatted view, not the Markdown source view.
2. Ctrl+click each link and compare with the **Ctrl+click** column:
   - **Dialog `X`**: the Open Link dialog opens and its box shows exactly `X`. Press Cancel.
   - **Note**: no dialog and no error box; the status bar shows "Only web links (http, https) open from here. Right-click a link to copy it."
   - **Jump to H**: the caret moves to heading H and the view scrolls to it.
   - **Nothing**: no dialog, no note, no jump.
3. Right-click the same link ▸ **Copy Link**, paste into Notepad, and compare with **Copy Link**. **None** means the menu has no Copy Link item.
4. Repeat a few in a read-only window (Edit ▸ Read Only, or the Help window): there a plain click does what Ctrl+click does (G7).
5. Any mismatch is a bug. Note the case number.

## Plain web links

| # | Link | Source | Ctrl+click | Copy Link |
|---|---|---|---|---|
| 1 | [basic https](https://example.com/) | `[basic https](https://example.com/)` | Dialog `https://example.com/` | `https://example.com/` |
| 2 | [plain http](http://example.org/page) | `[plain http](http://example.org/page)` | Dialog `http://example.org/page` | `http://example.org/page` |
| 3 | [uppercase scheme and host](HTTPS://EXAMPLE.COM/Path) | `[uppercase scheme and host](HTTPS://EXAMPLE.COM/Path)` | Dialog `https://example.com/Path` | `HTTPS://EXAMPLE.COM/Path` |
| 4 | [port](https://example.com:8443/x) | `[port](https://example.com:8443/x)` | Dialog `https://example.com:8443/x` | `https://example.com:8443/x` |
| 5 | [query and fragment](https://example.com/search?q=a+b&lang=en#results) | `[query and fragment](https://example.com/search?q=a+b&lang=en#results)` | Dialog `https://example.com/search?q=a+b&lang=en#results` | `https://example.com/search?q=a+b&lang=en#results` |
| 6 | [percent-encoded space](https://example.com/a%20b) | `[percent-encoded space](https://example.com/a%20b)` | Dialog `https://example.com/a%20b` | `https://example.com/a%20b` |
| 7 | [IPv6 literal](http://[2001:db8::1]/) | `[IPv6 literal](http://[2001:db8::1]/)` | Dialog `http://[2001:db8::1]/` | `http://[2001:db8::1]/` |
| 8 | [localhost](http://localhost:8080/) | `[localhost](http://localhost:8080/)` | Dialog `http://localhost:8080/` | `http://localhost:8080/` |
| 9 | [loopback IP](http://127.0.0.1/) | `[loopback IP](http://127.0.0.1/)` | Dialog `http://127.0.0.1/` | `http://127.0.0.1/` |
| 10 | [2048-character address](https://example.com/long/abcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabc) | `[2048-character address](https://example.com/long/abcdefghij…abc)` (2,048 characters) | Dialog with the whole address, ending `…hijabc`; the box scrolls | All 2,048 characters, ending `…hijabc` |

## Link forms

| # | Link | Source | Ctrl+click | Copy Link |
|---|---|---|---|---|
| 11 | <https://example.com/auto> | `<https://example.com/auto>` | Dialog `https://example.com/auto` | `https://example.com/auto` |
| 12 | www.example.com | `www.example.com` (bare text) | Dialog `http://www.example.com/` | `http://www.example.com` |
| 13 | https://example.org/bare | `https://example.org/bare` (bare text) | Dialog `https://example.org/bare` | `https://example.org/bare` |
| 14 | [full reference][ex] | `[full reference][ex]` with `[ex]: https://example.com/full-ref` | Dialog `https://example.com/full-ref` | `https://example.com/full-ref` |
| 15 | [collapsed reference][] | `[collapsed reference][]` | Dialog `https://example.com/collapsed-ref` | `https://example.com/collapsed-ref` |
| 16 | [shortcut reference] | `[shortcut reference]` | Dialog `https://example.com/shortcut-ref` | `https://example.com/shortcut-ref` |
| 17 | [with a title](https://example.com/titled "t") | `[with a title](https://example.com/titled "t")` | Dialog `https://example.com/titled` | `https://example.com/titled` |
| 18 | [angle brackets with a space](<https://example.com/a b>) | `[angle brackets with a space](<https://example.com/a b>)` | Note | `https://example.com/a b` |
| 19 | [escaped parentheses](https://example.com/a\(b\)) | `[escaped parentheses](https://example.com/a\(b\))` | Dialog `https://example.com/a(b)` | `https://example.com/a(b)` |

[ex]: https://example.com/full-ref
[collapsed reference]: https://example.com/collapsed-ref
[shortcut reference]: https://example.com/shortcut-ref

## Where links sit

These sit outside tables, so a right-click shows the ordinary text menu (Cut, Copy, Copy Link, Paste), except 24, which shows the table menu, and 28, the picture menu.

**20** **[bold link](https://example.com/bold)** · source `**[bold link](https://example.com/bold)**` · Ctrl+click: Dialog `https://example.com/bold` · Copy Link: `https://example.com/bold`

**21** [`code text`](https://example.com/code) · source `` [`code text`](https://example.com/code) `` · Ctrl+click: Dialog `https://example.com/code` · Copy Link: `https://example.com/code`

**22** `[in a code span](https://example.com/in-code)` · source `` `[in a code span](https://example.com/in-code)` `` · Ctrl+click: Nothing · Copy Link: None

**23** ~~[struck link](https://example.com/strike)~~ · source `~~[struck link](https://example.com/strike)~~` · Ctrl+click: Dialog `https://example.com/strike` · Copy Link: `https://example.com/strike`

| # | Link | Source | Ctrl+click | Copy Link |
|---|---|---|---|---|
| 24 | [table cell link](https://example.com/cell) | `[table cell link](https://example.com/cell)` | Dialog `https://example.com/cell` | `https://example.com/cell` |

### Heading with a [link](https://example.com/heading)

**25** The link in the heading above · source `### Heading with a [link](https://example.com/heading)` · Ctrl+click: Dialog `https://example.com/heading` · Copy Link: `https://example.com/heading`

> **26** [quoted link](https://example.com/quote) · source `[quoted link](https://example.com/quote)` · Ctrl+click: Dialog `https://example.com/quote` · Copy Link: `https://example.com/quote`

- **27** [list item link](https://example.com/list) · source `[list item link](https://example.com/list)` · Ctrl+click: Dialog `https://example.com/list` · Copy Link: `https://example.com/list`

**28** [![linked picture](data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAHgAAAAYCAIAAAC+8q7fAAAASUlEQVR4nO3QAQkAIADAMPNaxmpGsoXCHTzA2Zhr60Lj+cEngQbdCjToVqBBtwINuhVo0K1Ag24FGnQr0KBbgQbdCjToVqBBtzon4RZtGQaywwAAAABJRU5ErkJggg==)](https://example.com/picture) · source `[![linked picture](data:image/png;base64,…)](https://example.com/picture)` · Ctrl+click: Dialog `https://example.com/picture` · Copy Link: `https://example.com/picture`

## IDN and homographs

| # | Link | Source | Ctrl+click | Copy Link |
|---|---|---|---|---|
| 29 | [Unicode host](https://münchen.test/) | `[Unicode host](https://münchen.test/)` | Dialog `https://xn--mnchen-3ya.test/` | `https://münchen.test/` |
| 30 | [Cyrillic look-alike host](https://ехample.test/) | `[Cyrillic look-alike host](https://ехample.test/)` (е and х are Cyrillic) | Dialog `https://xn--ample-ywe6i.test/` | `https://ехample.test/` |
| 31 | [punycode host](https://xn--mnchen-3ya.test/) | `[punycode host](https://xn--mnchen-3ya.test/)` | Dialog `https://xn--mnchen-3ya.test/` | `https://xn--mnchen-3ya.test/` |
| 32 | [Cyrillic label ending in a hyphen](https://ехample-.test/) | `[Cyrillic label ending in a hyphen](https://ехample-.test/)` (е and х are Cyrillic) | Note, never an error box | `https://ехample-.test/` |

## Refused addresses

All show the note except 50, the percent-encoded twin of 49: there the dialog shows the encoding, so nothing is hidden.

| # | Link | Source | Ctrl+click | Copy Link |
|---|---|---|---|---|
| 33 | [user before host](https://user@example.com/) | `[user before host](https://user@example.com/)` | Note | `https://user@example.com/` |
| 34 | [host before @](https://example.com@evil.test/) | `[host before @](https://example.com@evil.test/)` | Note | `https://example.com@evil.test/` |
| 35 | [no slashes](https:example.com) | `[no slashes](https:example.com)` | Note | `https:example.com` |
| 36 | [scheme-relative](//example.com/) | `[scheme-relative](//example.com/)` | Note | `//example.com/` |
| 37 | [backslashes](https:\\\\example.com/) | `[backslashes](https:\\\\example.com/)` | Note | `https:\\example.com/` |
| 38 | [javascript](javascript:void(0)) | `[javascript](javascript:void(0))` | Note | `javascript:void(0)` |
| 39 | [mixed-case javascript](JaVaScRiPt:void(0)) | `[mixed-case javascript](JaVaScRiPt:void(0))` | Note | `JaVaScRiPt:void(0)` |
| 40 | [entity-encoded javascript](&#106;avascript:void(0)) | `[entity-encoded javascript](&#106;avascript:void(0))` | Note | `javascript:void(0)` |
| 41 | [data](data:text/plain,hello) | `[data](data:text/plain,hello)` | Note | `data:text/plain,hello` |
| 42 | [vbscript](vbscript:0) | `[vbscript](vbscript:0)` | Note | `vbscript:0` |
| 43 | [file](file:///C:/Windows/win.ini) | `[file](file:///C:/Windows/win.ini)` | Note | `file:///C:/Windows/win.ini` |
| 44 | [UNC path](\\\\server.invalid\\share) | `[UNC path](\\\\server.invalid\\share)` | Note | `\\server.invalid\share` |
| 45 | [ftp](ftp://ftp.example.com/readme.txt) | `[ftp](ftp://ftp.example.com/readme.txt)` | Note | `ftp://ftp.example.com/readme.txt` |
| 46 | [tel](tel:+15555550100) | `[tel](tel:+15555550100)` | Note | `tel:+15555550100` |
| 47 | [ms-settings](ms-settings:about) | `[ms-settings](ms-settings:about)` | Note | `ms-settings:about` |
| 48 | [search-ms](search-ms:query=example) | `[search-ms](search-ms:query=example)` | Note | `search-ms:query=example` |
| 49 | [RLO as an entity](https://example.com/file&#x202E;fdp.txt) | `[RLO as an entity](https://example.com/file&#x202E;fdp.txt)` | Note | `https://example.com/file` + U+202E + `fdp.txt` (Notepad shows `…filetxt.pdf`) |
| 50 | [RLO percent-encoded](https://example.com/file%E2%80%AEfdp.txt) | `[RLO percent-encoded](https://example.com/file%E2%80%AEfdp.txt)` | Dialog `https://example.com/file%E2%80%AEfdp.txt` | `https://example.com/file%E2%80%AEfdp.txt` |
| 51 | [zero-width space in host](https://exa&#x200B;mple.com/) | `[zero-width space in host](https://exa&#x200B;mple.com/)` | Note | `https://exa` + U+200B + `mple.com/` |
| 52 | [tab as an entity](https://example.com/a&#9;b) | `[tab as an entity](https://example.com/a&#9;b)` | Note | `https://example.com/a` + TAB + `b` |
| 53 | [2049-character address](https://example.com/long/abcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcd) | `[2049-character address](https://example.com/long/abcdefghij…abcd)` (2,049 characters) | Note | All 2,049 characters, ending `…abcd` |

## Hosts the link check refuses

A browser would read these differently from what the dialog could show, so they are refused. 77 to 80 are the near misses that still open.

| # | Link | Source | Ctrl+click | Copy Link |
|---|---|---|---|---|
| 68 | [leading hyphen](https://-a.test/) | `[leading hyphen](https://-a.test/)` | Note | `https://-a.test/` |
| 69 | [64-character label](https://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.test/) | `[64-character label](https://aaa…a.test/)` (a label of 64 `a`) | Note | the whole address |
| 70 | [Cyrillic label after a hyphen](https://a.-ех.test/) | `[Cyrillic label after a hyphen](https://a.-ех.test/)` | Note | `https://a.-ех.test/` |
| 71 | [Latin label ending in a hyphen](https://ü-.test/) | `[Latin label ending in a hyphen](https://ü-.test/)` | Note, never an error box | `https://ü-.test/` |
| 72 | [leading underscore](https://_dmarc.example.com/) | `[leading underscore](https://_dmarc.example.com/)` | Note | `https://_dmarc.example.com/` |
| 73 | [ends in a number](http://0127.0.0.1./) | `[ends in a number](http://0127.0.0.1./)` | Note | `http://0127.0.0.1./` |
| 74 | [loopback with a trailing dot](http://127.0.0.1./) | `[loopback with a trailing dot](http://127.0.0.1./)` | Note | `http://127.0.0.1./` |
| 75 | [hex host](http://0x/) | `[hex host](http://0x/)` | Note | `http://0x/` |
| 76 | [fullwidth zero](http://０127.0.0.1/) | `[fullwidth zero](http://０127.0.0.1/)` (the first digit is U+FF10) | Note | `http://０127.0.0.1/` |
| 77 | [decimal IPv4](http://2130706433/) | `[decimal IPv4](http://2130706433/)` | Dialog `http://127.0.0.1/` | `http://2130706433/` |
| 78 | [trailing-dot name](https://example.com./) | `[trailing-dot name](https://example.com./)` | Dialog `https://example.com./` | `https://example.com./` |
| 79 | [underscore inside a label](https://a_b.example.com/) | `[underscore inside a label](https://a_b.example.com/)` | Dialog `https://a_b.example.com/` | `https://a_b.example.com/` |
| 80 | [digit-ending label, not last](https://a1.example.com/) | `[digit-ending label, not last](https://a1.example.com/)` | Dialog `https://a1.example.com/` | `https://a1.example.com/` |

## Mail links

| # | Link | Source | Ctrl+click | Copy Link |
|---|---|---|---|---|
| 54 | [mail with a query](mailto:someone@example.com?subject=Hello%20there&body=Hi) | `[mail with a query](mailto:someone@example.com?subject=Hello%20there&body=Hi)` | Note | `mailto:someone@example.com?subject=Hello%20there&body=Hi` |
| 55 | [mail with a fragment](mailto:someone@example.com#section) | `[mail with a fragment](mailto:someone@example.com#section)` | Note | `mailto:someone@example.com#section` |

## Links within the document

| # | Link | Source | Ctrl+click | Copy Link |
|---|---|---|---|---|
| 56 | [to Link forms](#link-forms) | `[to Link forms](#link-forms)` | Jump to **Link forms** | `#link-forms` |
| 57 | [to a missing heading](#missing-heading) | `[to a missing heading](#missing-heading)` | Nothing | `#missing-heading` |
| 58 | [to the punctuation heading](#whats-new-v10--quotes--more) | `[to the punctuation heading](#whats-new-v10--quotes--more)` | Jump to **What's new? (v1.0) — "quotes" & more** | `#whats-new-v10--quotes--more` |
| 59 | [to the emoji heading](#launch--day) | `[to the emoji heading](#launch--day)` | Jump to **Launch 🚀 day** | `#launch--day` |
| 60 | [to the first Duplicate](#duplicate) | `[to the first Duplicate](#duplicate)` | Jump to **Duplicate** (the first) | `#duplicate` |
| 61 | [to the second Duplicate](#duplicate-1) | `[to the second Duplicate](#duplicate-1)` | Jump to **Duplicate** (the second) | `#duplicate-1` |

## Links to files

| # | Link | Source | Ctrl+click | Copy Link |
|---|---|---|---|---|
| 62 | [sibling file](other.md) | `[sibling file](other.md)` | Note | `other.md` |
| 63 | [parent folder file](../x.md) | `[parent folder file](../x.md)` | Note | `../x.md` |
| 64 | [file name with a space](<./a b.md>) | `[file name with a space](<./a b.md>)` | Note | `./a b.md` |

## Raw HTML

**65** Block-level raw HTML, below · source `<p><a href="https://example.com/raw-block">raw block link</a></p>` · Ctrl+click: Note · Copy Link: None

<p><a href="https://example.com/raw-block">raw block link</a></p>

**66** Inline <a href="https://example.com/raw-inline">raw inline link</a> words · source `<a href="https://example.com/raw-inline">raw inline link</a>` · Ctrl+click on the words: Nothing (known limit: an inline raw-HTML `<a>` isn't clickable) · Copy Link: None

**67** Block-level raw HTML anchor, below · source `<p><a href="#launch--day">raw block anchor</a></p>` · Ctrl+click: Jump to **Launch 🚀 day** · Copy Link: None

<p><a href="#launch--day">raw block anchor</a></p>

## Gestures

- **G1** In the editable view, a plain click (no Ctrl) on case 1 only places the caret: no dialog, no note.
- **G2** Ctrl+double-click case 1: one dialog, not two.
- **G3** In the dialog, Enter and Esc both cancel (Cancel is the default), and Alt+O does nothing. **Open** launches the browser. For case 10 the whole address is in the box, which wraps and scrolls.
- **G4** Put the caret inside the text of case 1 with the arrow keys, then press Shift+F10 or the Menu key: one menu opens, Copy Link is on it and copies `https://example.com/`. With the caret in plain words, no Copy Link.
- **G5** With spell check on, right-click the squiggled word [Qwzxvy](https://example.com/spelling): the spelling menu includes Copy Link, which copies `https://example.com/spelling`.
- **G6** Right-click the picture in case 28 in the editable view: the menu shows Resize… and Copy Link.
- **G7** Turn on Edit ▸ Read Only. A plain click on cases 1, 33 and 56 gives the Ctrl+click result, and a right-click on the table link in case 24 shows the text menu with Copy Link.

## Notes

- The page hands the app only addresses that start with `http://` or `https://`. The app then refuses user names before the host, spaces and hidden characters, hosts that aren't a DNS name or IP address, and hosts a browser would read as a number. The status bar note is the same either way.
- Copy Link copies the address as the markdown writes it, with entities and escapes decoded. The dialog shows the address the app will open, host in punycode, so the two can differ (3, 12, 29, 77).

## Anchor targets

### What's new? (v1.0) — "quotes" & more

### Launch 🚀 day

### Duplicate

### Duplicate
