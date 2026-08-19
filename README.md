# win-explorer-force-tabs

Small Windows utility that forces newly opened File Explorer windows into tabs of an existing Explorer window.

Windows 11 already opens many Explorer actions in tabs, but some actions, shortcuts or applications can still create a separate Explorer window. This utility is intended to catch those cases and move the newly opened location into a tab instead.

The first Explorer window stays independent. Subsequent File Explorer windows are converted into tabs when possible.

> Tested on **Windows 11 only**.

## How it works

Windows uses `explorer.exe` for File Explorer but also for other Shell windows, so the process name and window class alone are not enough to identify a real File Explorer window.

The program therefore checks for the actual Windows 11 File Explorer tab interface exposed through Microsoft UI Automation (`TabView`, `TabListView`, `TabItem` and `AddButton`). A window is handled only when this tab interface is present. This allows normal and virtual Explorer locations such as Home or This PC to be recognized while other Shell windows are left untouched.

When the opened location is the Windows user profile root (`%USERPROFILE%`), the utility preserves the special **User Files** Shell view instead of reopening the physical path directly. This matters when known folders such as Desktop, Documents, Downloads, Music, Pictures or Videos have been redirected to other drives: Explorer keeps the normal user-folder presentation and the redirected locations are displayed correctly.

## Requirements

- Windows 11
- .NET 6 SDK

## Log

A diagnostic log is written to:

```text
%TEMP%\win-explorer-force-tabs.log
```

It records the main steps used to detect a File Explorer window, create the destination tab, navigate to the requested location and close the temporary source window. It is useful when a window is not converted as expected.

## Origin

This project is directly derived from the tab-management approach used by **ExplorerTabUtility** by w4po:

https://github.com/w4po/ExplorerTabUtility

The COM/Explorer interaction and internal tab commands used here are based in part on that project. This utility adds a different goal and workflow: automatically catching newly created Explorer windows and redirecting them into tabs, with additional window qualification for Windows 11 Shell windows.

See `THIRD_PARTY_NOTICE.md` for attribution and license information.

## Version

Current version: **v1.0.3**.

### v1.0.3

- Preserve the Windows **User Files** Shell view when `%USERPROFILE%` is opened.
- Keep the expected Explorer presentation when known user folders have been redirected to other drives.
- Continue to use ordinary filesystem paths for normal folders and Shell namespace destinations for virtual Explorer locations.

Initial release: **v1.0.0**.

## License

GPL-3.0-or-later. See `LICENSE`.
