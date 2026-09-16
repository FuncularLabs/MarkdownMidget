# Get .md files opening in Markdown Midget again after updating from 1.0.0-rc1

## Who this is for

This guide is for you if you used Markdown Midget's own update to go from 1.0.0-rc1
to 1.0.0-rc2. For some people, that update switched `.md` files back to another app.
If double-clicking a `.md` file now opens Notepad, Markdown Monster or another app
instead of Markdown Midget, this guide gets it back.

Windows lets only you choose which app opens a file type, so Markdown Midget can't
change it for you. It takes you to the right place in Windows Settings instead.

## The fastest way: use the notice

After the update, Markdown Midget opens and shows a notice that says
"Windows doesn't open .md files with Markdown Midget."

![The notice after updating](images/rc1-fix-01-notice.png)

1. Click **Make it the default…**. Windows Settings opens on Markdown Midget's page
   under **Apps ▸ Default apps**. If you use Windows 10 or an early Windows 11, see
   [On Windows 10 or an early Windows 11](#on-windows-10-or-an-early-windows-11).
2. The page lists the file types Markdown Midget can open, each with the app that
   opens it now. Click **.md**.
3. Select **Markdown Midget**, then click **Set default**. On some versions of
   Windows, the button is **OK**.

![Markdown Midget's page in Windows Settings](images/rc1-fix-02-settings-app-page.png)

![Choosing Markdown Midget for .md files](images/rc1-fix-03-choose-app.png)

The page also lists `.markdown`, and `.mdenc` for encrypted documents. If you use
those, set them the same way. The Settings wording may differ slightly by Windows
version.

If Markdown Midget opens with unsaved work, a recovered document or the Help window,
the notice waits. It appears the next time Markdown Midget opens without them.

## If you closed the notice

The notice appears only once. To get to the same Settings page later:

1. In Markdown Midget, open **File ▸ Windows Integration ▸ Make Markdown Midget the default…**.
2. Follow steps 2 and 3 in the previous section.

![The File menu with Make Markdown Midget the default… highlighted](images/rc1-fix-04-file-menu.png)

## If Markdown Midget isn't listed in Settings

Windows lists Markdown Midget only after it's registered. If it isn't registered,
Markdown Midget says so when you click **Make it the default…**. To register it:

1. Open **File ▸ Windows Integration ▸ Register as .md editor…**.
2. Set **Add to the Start menu** and **Add a Desktop shortcut** the way you want
   them, then click **Register**.
3. Click **OK** on the message that confirms it.
4. Open **File ▸ Windows Integration ▸ Make Markdown Midget the default…** again, and
   follow the Settings steps above.

The **Register as .md editor** window also has a **Make Markdown Midget the default…**
button. It works once Markdown Midget is registered.

![The Register as .md editor window](images/rc1-fix-05-register-dialog.png)

## On Windows 10 or an early Windows 11

On these versions, **Make it the default…** opens the main **Default apps** page
instead of Markdown Midget's own page.

- **Windows 10:** Click **Choose default apps by file type**. Scroll to **.md**, click
  the app next to it, and select **Markdown Midget**.
- **Windows 11 before version 22H2:** Type `.md` in the search box at the top of the
  page, click the app it shows, and select **Markdown Midget**.

The wording may differ slightly by Windows version.

![Choose default apps by file type on Windows 10](images/rc1-fix-06-windows10-by-file-type.png)

## If nothing else works: choose it in File Explorer

1. Right-click any `.md` file and select **Open with ▸ Choose another app**.
2. Select **Markdown Midget**. If it isn't in the list, register it first, as
   described earlier.
3. Click **Always**. On Windows 10, select **Always use this app to open .md files**,
   then click **OK**.

## Will this happen again?

No. From 1.0.0-rc2 on, updating Markdown Midget or registering it again keeps the app
you chose for `.md` files. This one time, the update to rc2 was done by rc1.

If `.md` files ever stop opening in Markdown Midget again, the first start after the
next update tells you, with the same **Make it the default…** button. If you ticked
**Don't show this again**, the notice stays off.
