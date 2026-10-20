# Publish the legacy WPF prototype

This script publishes the preserved `VoiceAssistant.Desktop` microphone-only
prototype. It is not the recommended current Windows path. For microphone plus
tab/system audio in the current `/live.html` UI, publish
[`VoiceAssistant.WebOverlay`](../../src/VoiceAssistant.WebOverlay/README.md).

Run from a Windows checkout with the .NET 8 SDK:

```powershell
.\scripts\desktop\Publish.ps1 -OutputDirectory C:\Apps\MeetingCoach
```

Use a new or empty folder outside the repository. The script does not remove
existing files. It publishes a self-contained Windows x64 app, so the recipient
does not need to install the .NET runtime. The deployment values (endpoint,
origin, tenant, client ID, scope) come from the git-ignored
`src\VoiceAssistant.Desktop\appsettings.local.json`, copied from
`appsettings.local.example.json`; the script warns when the published folder
lacks it. Copy the **whole output folder** and
double-click `Open-Meeting-Coach.cmd`. A single executable without its companion
files is not a complete distribution.
The app opens directly into the overlay, without starting audio capture.
Right-click it for secondary settings/login/materials and session controls.
Closing settings returns to the overlay; use the menu's application-exit action
to stop sessions and release resources.

Double-click `Preview-Offline.cmd` to inspect the overlay and practice with
clearly labeled canned content. This mode needs no sign-in or microphone and
does not use Azure services. It is not a substitute for live acceptance.

For a machine that already has the .NET 8 Windows Desktop runtime:

```powershell
.\scripts\desktop\Publish.ps1 -OutputDirectory C:\Apps\MeetingCoach-Test `
  -FrameworkDependent
```

An existing SDK outside PATH can be selected with `-DotnetCommand
C:\Tools\dotnet\dotnet.exe`. The app's own configuration and Entra login are
separate from the operator's Azure CLI login. See
`src\VoiceAssistant.Desktop\README.md` for audio consent, device selection,
PowerPoint overlay controls, practice and configuration.

This command builds software; it does not launch it, record audio, sign in,
create a shortcut, register a service or change operating-system settings.
