# TextDock

Your phone's texts in a small Windows window that stays on top of everything and follows you to every virtual desktop.

TextDock is a desktop shell for Messages for web, the site an Android phone pairs with to text from a computer. It loads the real site in Microsoft Edge WebView2, trims the layout down, and adds what a browser tab can't do: always on top, all desktops, a global hotkey, tray notifications, bulk delete, and an orange alert when someone you care about is waiting on a reply.

## Download

**[Download TextDock.exe](https://github.com/C-Squared-Solutions-LLC/TextDock/releases/latest/download/TextDock.exe)** (or pick a version on the [releases page](https://github.com/C-Squared-Solutions-LLC/TextDock/releases)) and run it. One file, .NET included, no installer.

- Windows 10 or 11, x64.
- Needs the WebView2 runtime. Windows 11 ships with it, and Edge updates have put it on most Windows 10 PCs. If yours lacks it, TextDock says so and tells you where to get it.
- The exe isn't code-signed, so SmartScreen may block the first launch with "Windows protected your PC". Click **More info**, then **Run anyway**. Each release has a `SHA256SUMS.txt` to check the download against.

The first launch shows a QR code. Scan it from the Messages app on your phone (Device pairing). The pairing survives restarts.

To upgrade, quit TextDock from the tray menu and replace the exe. Settings and the pairing live in your user profile, not next to the exe.

## Window

| Control | Does |
|---|---|
| ☰ | Messages' own menu (settings, theme, unpair). Only shown while the compact layout hides the page's top bar. |
| ⋯ | App menu. Same as right-clicking the tray icon. |
| ⧉ | Show on all virtual desktops (on by default) |
| 📌 | Always on top (on by default) |
| ✕ | Hides to the tray. Quit from the tray menu. |

- **Ctrl+Alt+M** shows or hides the window from anywhere. Change it with `Hotkey` in `settings.json`.
- Ctrl+wheel or Ctrl+plus/minus zooms. The level sticks (default 90%).
- Drag the title bar to move; all edges resize; Win+arrow snapping works.
- Start with Windows is in the menu. It launches straight to the tray.

## Compact layout

On by default (menu > Compact layout). It:

- drops the page's 64px logo/account bar, moving its menu to ☰
- trims list rows from 72px to about 44px, with smaller avatars
- slims the conversation header to 46px
- tightens message bubbles and lets them use the full width; the react/reply/more buttons float on hover instead of reserving a 116px column
- puts emoji/sticker/GIF and the text box on one row, so the compose bar is 44px instead of 130px
- hides promo banners. Offline, battery and "trying to reach your phone" warnings stay.

The title bar follows the page's light/dark theme.

To tweak it, use menu > Edit custom CSS. That opens `%APPDATA%\TextDock\custom.css`, which reloads on save and loads after the built-in rules. The built-in sheet exposes variables:

```css
html.tdk-compact {
  --tdk-list-avatar: 34px;
  --tdk-msg-avatar: 28px;
  --tdk-row-pad: 7px;
  --tdk-header-h: 46px;
  --tdk-icon-btn: 34px;
}
```

F12 opens DevTools for finding selectors.

## Selecting and deleting conversations

Messages for web only deletes one conversation at a time. TextDock adds a selection mode to the list:

- **Select** (icon next to Start chat), or Ctrl+click any conversation. Clicks then tick rows instead of opening them. Esc or ✕ leaves.
- **Unknown** loads the whole list and ticks every conversation whose name is just a phone number, short code or email address, meaning there's no contact for it. Group chats and pinned conversations are never ticked automatically. Business senders that show a name can't be told apart from contacts, so they're skipped too. Untick anything you want to keep.
- **All** ticks every loaded conversation.
- **Delete** asks first in a Windows dialog listing what goes. Then it runs Messages' own menu item for each conversation ("Move to trash" when Messages' Trash is on, otherwise "Delete"), one at a time, with a Stop button.

This acts on your phone too. If the Messages menu ever stops matching what TextDock expects, the run stops instead of guessing.

## Important conversations

Click the ☆ in a conversation's header to mark it important; starred conversations also get a small ★ in the list. When one of them gets a message from the other person, TextDock treats it as waiting on you:

- the title bar pulses orange and the window border turns orange
- the title shows "*Name* needs a reply". Click it to open the conversation; ✕ dismisses it until their next message.
- a minimized window flashes its taskbar button; a window hidden in the tray blinks its tray icon orange
- the conversation's row gets an orange edge in the list

It stops when you reply. While that conversation is open in the focused window, it stays still. "You replied" is read from the list, where Messages starts your own last message with "You:"; that check assumes Messages is in English. Reactions don't count as new messages.

Turn the blinking off with menu > Blink for important conversations.

## Notifications

New messages show up as Windows notifications from the tray icon. Clicking one opens TextDock on that conversation. The page's own chime is muted while these are on, so you hear one sound, and Do Not Disturb applies. Turn them off with menu > Message notifications.

Unread conversations show as a badge in the title bar, a taskbar overlay and a dot on the tray icon.

## Privacy

TextDock has no server and no telemetry. Its only network traffic is the Messages web page (messages.google.com) and its sign-in pages, exactly as in a browser tab. The log records app events (started, pinned, notification shown), never message text or names. Starred conversations are stored as the ids from the page URL.

| Path | What |
|---|---|
| `%APPDATA%\TextDock\settings.json` | Toggles, zoom, hotkey, window position, starred conversation ids |
| `%APPDATA%\TextDock\custom.css` | Your style overrides |
| `%LOCALAPPDATA%\TextDock\WebView2\` | Browser profile, including the pairing |
| `%LOCALAPPDATA%\TextDock\textdock.log` | App events only |

Delete those two folders to remove every trace, after turning off Start with Windows.

## How it works

- **Always on top.** WPF `Topmost`. It's re-asserted whenever the window is brought back.
- **All desktops.** This uses the shell's `IVirtualDesktopPinnedApps`, the same undocumented call Task View makes for "Show this window on all desktops". It's re-applied every time the window is shown, because a hidden window loses its pin. If a Windows update breaks the interface, TextDock falls back to a tool window. Tool windows also appear on every desktop, but lose their taskbar button.
- **Frame.** The caption is cut out in `WM_NCCALCSIZE`. The side and bottom borders stay native (invisible on Windows 11), so resizing, snapping, the shadow and rounded corners all come from Windows.
- **Page integration.** `Assets/inject.js` runs before the page's own scripts. It injects `Assets/compact.css` and reports the theme color and unread count to the window. It also routes service-worker notifications to the app and runs the selection, delete and important-conversation features.
- **Background.** Chromium's background throttling is off, so messages arrive on time while the window sits in the tray.

## Building

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
.\publish.ps1                      # dist\TextDock.exe, standalone like the release (quits a running copy first)
.\publish.ps1 -FrameworkDependent  # ~2 MB exe that needs the .NET 10 Desktop Runtime
.\publish.ps1 -Shortcut            # also adds a Start menu shortcut
.\tools\make-icon.ps1              # regenerates Assets\app.ico
```

`TextDock.exe --quit` closes a running instance cleanly.

Releases come from GitHub Actions (`.github/workflows/build.yml`). Every push builds the exe and keeps it as a workflow artifact for 14 days. Pushing a version tag also publishes a release:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

### Debug port

`TextDock.exe --debug-port=9222` opens the Chrome DevTools protocol on localhost. While it's on, any local process can read and drive your Messages session. Use it for debugging only.

## License

MIT. See [LICENSE](LICENSE), and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for what the release exe bundles.

TextDock is made by C Squared Solutions LLC. It is an independent project, not affiliated with or endorsed by Google. Google Messages is a trademark of Google LLC.
