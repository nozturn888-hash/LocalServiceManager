# Local Service Manager

[简体中文](README.md) · [Release notes](RELEASE-NOTES.md) · [MIT license](LICENSE)

Local Service Manager is a Windows desktop and system tray app for managing local services started by scripts. It uses C#, WinForms, and system-provided .NET Framework assemblies. It has no NuGet dependencies and installs no additional background component.

The public edition provides generic background services, a ComfyUI inspection template, and registration for client-managed stdio MCP servers. Install your services, Python environments, models, and MCP clients separately; they are not included.

## Local customized edition and public release

**The local customized installation retains its existing control capabilities and service entries. Public v0.1.0 is a separate, generic portable release. It is not a complete migration of that customized environment and does not include ready-to-run copies of its five local entries.**

Installation-specific control depends on external runtimes, controller process records, APIs, or credential chains as well as paths. These integrations have therefore not been moved directly into the public core:

| Capability | Local customized edition | Public v0.1.0 |
| --- | --- | --- |
| Gateway takeover of external instances | Existing dedicated control retained | The GUI does not take over external processes; an explicitly configured script example is provided separately |
| Local Codex Bridge profile and credential chain | Local adapter retained | Adapter, local profile, and credential chain are not distributed |
| Workbench-specific queue and atomic shutdown API | Local adapter retained | This API integration is not included |
| Personal ComfyUI Desktop control scripts | Existing local scripts retained | Source rights remain unverified, so scripts are not distributed; editable template and queue guards remain, using your own scripts |
| Initial service configuration | Existing local entries retained | Empty list; both startup layers disabled |

The [Hermes Gateway script example](examples/hermes-gateway/README.md) retains exact identity checks and an official process-record stop guard. It requires explicit `InstallRoot` and `Action` values, has no actual installation-path default, and is not enabled automatically. You may configure it as your own control script; it does not grant external-process takeover to generic services in the public GUI.

The public edition rejects import or execution of old private-adapter configurations. Do not treat the local configuration as a direct public-edition migration.

## Run the portable package

Requires Windows x64 and .NET Framework 4.x. The interface uses Windows system fonts.

1. Obtain `LocalServiceManager-v0.1.0-win-x64.zip` and `SHA256SUMS.txt` from [Releases](https://github.com/nozturn888-hash/LocalServiceManager/releases).
2. Extract the full package to a writable local directory.
3. Run `LocalServiceManager.exe` in that directory.

Check the downloaded archive with PowerShell:

```powershell
Get-FileHash .\LocalServiceManager-v0.1.0-win-x64.zip -Algorithm SHA256
```

Compare the result with `SHA256SUMS.txt` from the same version. App data stays beside the EXE, so you can back up the entire directory:

```text
LocalServiceManager.exe
config/    Service settings and interface language preference
logs/      Captured output and app records
icons/     Copied custom icons
scripts/   Your configured service control scripts
examples/  Explicitly configured control examples, not enabled automatically
```

The initial service list is empty. Both app startup at Windows sign-in and service startup with the app are off by default.

## Add and use services

Choose Add, select a management type, and enter the settings. You can also drop a start script onto the window. Dropping only prefills a draft; it does not save or run the script. Entries can be edited, deleted, and reordered by dragging. The tray uses the same order.

| Type | Scope |
| --- | --- |
| Independent background service | Supply your own start, stop, and optional restart scripts, working directory, arguments, full service EXE path, unique command-line fragment, and health checks. |
| ComfyUI template | Prefills port 8188, HTTP inspection, and queue protection. Supply your own start/stop scripts and exact process identity. |
| Client-managed stdio MCP | Record the owning client, command, arguments, and notes. The client manages its lifecycle. This app does not independently start or stop it, read its protocol stream, or modify client settings. |

Review the scripts, arguments, and process identity, enable execution permission, and save before operating an independent service. Saving settings and starting a service are separate actions.

Green means running, red means stopped, and yellow means a transition. Unknown and error states include an explanation. Status uses process identity, port inspection, and optional HTTP checks; script exit does not prove service health. Start does not relaunch an already running service. Operations for the same service are serialized, and restart waits for stop to finish.

The public edition treats externally started generic and ComfyUI instances as read-only. App-managed processes must pass identity checks before they can be stopped. The app does not terminate unrelated processes based only on a port or the name `python.exe`. ComfyUI stop and restart are protected while tasks are running or queued; stop is also refused when the queue cannot be verified. Your control scripts must respect these boundaries too.

Start scripts normally run in the background without a console window. Scripts that require input or open their own windows need adaptation; hiding the console cannot remove those requirements. Paths may contain spaces and Chinese characters. Script arguments are passed as entered; quote argument paths containing spaces.

An entry can have a web URL or local application as its Open entry target. Double-click, Enter, and Open entry in the tray only open that target. They do not start or restart the service.

## Logs, icons, and configuration

The built-in log view supports captured stdout/stderr or tailing an existing file, with search, copy, and paused scrolling. The cache is bounded and captured logs rotate. File encoding can be UTF-8, GB18030, UTF-16, or Big5. Original service logs are user content and are not translated with the interface.

Use built-in icons or extract icons from images, ICO, EXE, or DLL files. Multiple resources can be selected, and chosen icons are copied into the app directory. Extracting resources does not run the program. The status dot is separate from the icon.

Import adds settings without executing entries; imported service startup and execution permission are disabled. Export removes script/application arguments, identity command fragments, process records, registration notes, and URL query information, and uses built-in icons. Names, paths, and other fields remain, so review free text before sharing. Export exchanges safer configuration; it is not a substitute for a full local backup.

## Language, startup, and exit

Choose Simplified Chinese or English under Tool settings → Interface language. The choice is saved immediately and updates app-owned titles, the main interface, editors, settings, tray menus, prompts, and template text. Selection, order, and input drafts are preserved. User-defined names, notes, commands, paths, URLs, and logs are not translated. Native file-dialog buttons and system-generated text follow the language of Windows or the relevant component.

The two startup settings are independent:

- App startup: Start this tool in the tray when signing in to Windows, in Tool settings.
- Service startup: Start with this app (separate from Windows sign-in startup), under the service's Settings → Start and stop. Settings are read-only while the service runs or an operation is in progress; stop it before changing and saving this option. The client handles stdio entries, which have no independent startup option.

Closing the main window hides it in the tray. Polling slows while hidden. The tray shows service status and offers the permitted start, stop, and restart actions.

- Exit tool (keep services running) closes the manager and leaves services running.
- Stop services managed by this tool and exit first attempts to stop services that the app manages and can safely control. If identity or task protection prevents stopping, the app reports it and preserves the current state. It does not force-terminate external processes.

## Build from source

Build with the system .NET Framework C# compiler and Windows PowerShell. No NuGet restore is needed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The portable app is written to `dist/LocalServiceManager-v0.1.0-win-x64/`, with the EXE at its root.

Compile the isolated tests:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Tests
.\tests\Tests.exe
```

`-Tests` compiles the test programs only. Explicitly running `Tests.exe` runs isolated lifecycle, GUI, and tray checks. The tests use isolated fixtures and do not operate your real services.

## First-version limits

v0.1.0 has no automatic recovery restart. Installation-specific adapters from the development machine are not distributed. Other services can use your generic scripts; stdio servers remain client-managed. Review scripts, paths, ports, and process identities on each machine.

See [release notes](RELEASE-NOTES.md) for the verification scope. Release acceptance has not operated real business services or written actual Windows startup settings. No fixed idle CPU or memory figure is promised.

The code is licensed under [MIT](LICENSE).
