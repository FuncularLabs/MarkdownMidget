# Community themes

Themes for Markdown Midget made by people who use it. They don't ship with the
app: to use one, copy it in yourself.

| File | Menu name | By |
|---|---|---|
| `Amber-Phosphor-CRT.css` | Amber Phosphor CRT | Joe Sparks (@joesparks on X) |
| `Code-Breaker.css` | Code Breaker | Joe Sparks (@joesparks on X) |
| `Patriot.css` | Patriot | Joe Sparks (@joesparks on X) |

## The themes

**Amber Phosphor CRT** is an old amber terminal screen. Its colours are the
built-in **Amber Phosphor** palette, amber with no blue at all; on top of that
it draws scanlines and a darkened edge like a curved tube across the page,
gives the text a soft glow, and makes the three largest heading levels glow
brighter and dimmer on a slow seven-second cycle. It is Joe's original file,
renamed so that it sits beside the built-in in **View ▸ Theme** instead of
replacing it.

**Code Breaker** is green on black, and the document arrives encrypted:
paragraphs, list items and table cells are blurred into unreadable glow until
you point at one, which decodes with a flicker and blurs again when you move
away. Code blocks show dots until you point at them. Headings stay readable,
in capitals, so you can find your way around, and everything is in a
monospaced font. It is a game, not a reading theme: switch to another theme to
read normally. Nothing in the file changes, only what the screen shows.

**Patriot** is red, white and blue: white text on deep navy, red for the three
largest heading levels, the list markers and the quote bar, blue for the
smaller headings and the links. Beyond the colours, it keeps the text cursor
red and sets its own underline on links.

Amber Phosphor CRT and Code Breaker also tint diagrams to match. All three
carry rules for list markers and inline code that make them look the same on
versions of Markdown Midget before 1.0.0-rc3, which had no variables for
either.

## Installing one

1. Download the `.css` file.
2. In Markdown Midget, choose **View ▸ Theme ▸ Open Themes Folder**. It opens
   your `custom` themes folder.
3. Copy the file into that folder. It appears in **View ▸ Theme** at once, under
   the name in the table above.

Keep the file's name. A file in `custom` with the same name as a built-in theme
takes the built-in's place in the menu.

## Not tested like the built-ins

The built-in themes are checked on every build: their text is measured for
contrast, and they must set every colour the editor draws. Community themes
aren't. The only checks here are that the app accepts each file, so its menu
entry isn't greyed out, and that none takes a built-in's place. They style the
editor with rules of their own, and a later version of Markdown Midget can
change how those look.

## Writing your own

Start from `sample.css`, which Markdown Midget puts in your `custom` folder
(it is also [here in the repository](../src/MarkdownMidget/Themes/sample.css)).
It lists every colour a theme can set, and Help's
[Writing your own](../HELP.md#writing-your-own) section explains the rest,
including what a theme can't do.
