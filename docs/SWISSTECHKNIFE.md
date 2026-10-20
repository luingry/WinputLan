# SwissTechKnife module (`winput-lan`)

`src/WinputLan.Module` packages Winput LAN as a [SwissTechKnife](https://github.com/luingry/SwissTechKnife) module: `IBackgroundModule`, `activation: startup`, `loadMode: memory`. The module is a thin supervisor in the host process; the app itself runs in its own process in **hosted mode**, so input hooks, `SendInput`, sockets and WebView2 never touch the host's UI thread and an app crash never takes the host down.

```powershell
powershell -File scripts/pack-module.ps1   # artifacts\winput-lan-<VERSION>.zip + sha256/size
```

Package layout (module version = `VERSION`):

```text
module.json, WinputLan.Module.dll       loaded by the host (depends only on SwissTechKnife.Sdk, never packaged)
app/WinputLan.Hosted.exe (+ .config)    the regular WinputLan.exe, renamed so it is never mistaken for the standalone app
app/WinputLan.Core.dll, Microsoft.Web.WebView2.*.dll, app/runtimes/win-*/native/WebView2Loader.dll
```

`loadMode` stays `memory`: the module assembly references nothing but the SDK, and WebView2 (with its native loader) is loaded only by the child process from `app/`, so the host never needs `Assembly.Location` or side-by-side native DLLs for this module. Memory mode also keeps the package folder unlocked, so uninstall/update apply at once.

`src/WinputLan.Module/lib/SwissTechKnife.Sdk.dll` is a compile-only reference copied from a SwissTechKnife build. STK's `scripts/pack-modules.ps1` picks up `artifacts/winput-lan-<version>.zip` for local feed testing. Every repack changes the hash (zip timestamps), so publish exactly the zip whose hash is in the catalog.

## Hosted mode

`WinputLan.Hosted.exe --hosted --data "<module data folder>" --parent <host pid> [--show]`

- Data (config, pairings, device identity `identity.pfx.dpapi`, WebView2 report profile) lives in the module data folder. The first hosted run copies `config.json` and the identity from `%LOCALAPPDATA%\WinputLan` once (marker `.migrated`), so paired PCs keep recognizing this one. The standalone files are never modified.
- No tray icon, no update check/install, no Run-key or logon-task registration, no "continue in background" option: closing the window only hides it; the module page's **Abrir Winput LAN** brings it back.
- Exits (normal cleanup) when the module sets `Local\WinputLan.Hosted.Exit` or the host process ends.
- Same single-instance mutex as the standalone app (`Local\WinputLan.<sid>.Instance`): standalone and hosted never run together. A hosted start that loses the mutex exits with code 3; the module shows **Aberto fora do SwissTechKnife** and takes over when the standalone app exits.
- Writes its pid to `<data>\hosted.pid` (the module needs it when the task scheduler started the process).

Exit codes the module understands: `3` another instance runs, `4` restart requested (administrator control changed), anything else unexpected is a crash (restarted after 0/1/2 s, at most 3 times a minute, then **Parado** until the user opens it).

Standalone behavior is unchanged when `--hosted` is absent.

## Elevation

The STK host runs non-elevated and must never show a UAC prompt on startup. The standalone app has two elevated pieces: the installer's firewall rule (program-scoped to `Program Files\Winput LAN\WinputLan.exe`) and the opt-in **Permitir controlar apps de administrador**, which relaunches through UAC and uses a `/RL HIGHEST` logon task for auto-start. Neither fits the module: the package folder is per-user, writable and changes with every version (`Modules\winput-lan\<version>\`).

Decision:

| Piece | Hosted design |
|---|---|
| Normal run | The module runs a **stable copy** of the package in `<data>\runtime\` (refreshed silently when the package changes). Non-elevated, so a user-writable location is fine. |
| Firewall | Rule `Winput LAN (SwissTechKnife, Private TCP)`, Private profile, the configured TCP port, scoped to `<data>\runtime\WinputLan.Hosted.exe` and to the admin-only copy. Because the runtime path is stable, it is created once, not per update. The module checks for it read-only (`netsh ... show rule`) after each start; when it is missing the page shows **Permitir conexões de rede**, which runs the elevated helper (one UAC prompt, on that click). The helper also deletes block rules Windows adds when its firewall alert is dismissed. Until the rule exists, Windows may show its own firewall alert the first time the app listens. |
| Admin control on | The app's checkbox, in hosted mode, starts `WinputLan.Hosted.exe --hosted-admin enable --data <dir>` with UAC (that click is the only prompt). The helper copies the build to `%ProgramFiles%\Winput LAN (SwissTechKnife)\` (admin-only: a normal process cannot replace or rename it, so the task is not a silent elevation path), registers the trigger-less task `Winput LAN (SwissTechKnife)` (`InteractiveToken`, `HighestAvailable`, no time limit, normal priority, parallel instances) and the firewall rules, then the app exits with code 4. |
| Startup with admin control | The module sees the task (Task Scheduler COM, read-only) and an up-to-date Program Files copy, and runs the task with `RunEx`: `$(Arg0)` = host pid, `$(Arg1)` = `--show`/`--hidden`. Running a task the user registered needs no elevation, so there is no UAC prompt. The app then publishes its pid in `hosted.pid`. |
| Stopping an elevated copy | A normal process cannot terminate or fully open an elevated one. The module creates and keeps the exit event (an event created by the elevated process would not be signalable), waits on the process with `SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION` only, and the app also exits when the host process ends. The app creates the same event with a user/medium-integrity DACL when it gets there first. |
| After a module update | The Program Files copy is older than the package, so the module runs the new version non-elevated and the page shows **Confirmar permissão de administrador** (one UAC prompt on that click refreshes the copy, then the module restarts the app elevated). It never runs a stale elevated copy. |
| Admin control off | The elevated app deletes the task itself (no prompt) and exits with code 4; the module restarts it non-elevated. The Program Files copy stays, inert without the task. |

Limits: uninstalling the module from STK cannot remove the task, the Program Files copy or the firewall rules (the SDK has no uninstall hook and removing them needs elevation). Turning admin control off first removes the task; the rest is inert. The task is per user; the Program Files copy is shared by all users of the PC.
