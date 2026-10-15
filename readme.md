# Pointer

A simple Windows system-tray app for drawing temporary, glowing lines over your desktop. Use it as a laser pointer during presentations, screen sharing, or demonstrations without editing the content underneath.

## Download and run

[Download Pointer.exe for Windows x64](https://github.com/alperozcelik/pointer/releases/download/latest/Pointer.exe)

Run the executable and look for the colored dot in the system tray. Check the tray overflow menu if the icon is hidden.

- No installer or separate .NET installation is required for the published executable.
- Double-click the tray icon, or right-click it and choose **Settings...**, to configure the app.
- Right-click the tray icon and choose **Exit** to close it.

The download is a compressed, self-contained executable. The workflow does not currently code-sign it, so Windows may display an unknown-publisher or SmartScreen warning. Only run downloads from a source you trust.

## Default controls

**Win** means the Windows logo key. Hold the keyboard combination while pressing and dragging the mouse button.

| Action | Default binding |
|---|---|
| Draw freehand | Ctrl + Win + left-drag |
| Draw a straight line | Ctrl + Win + right-drag |
| Temporarily make a freehand segment straight | Hold Shift while drawing |
| Change line width | Ctrl + Win + mouse wheel |
| Cycle through the color palette | Ctrl + Win + Shift + mouse wheel |

Freehand strokes begin their fade countdown when you release the mouse button. Straight-line strokes stay visible while their binding's modifiers remain held, then fade together after you release the combination.

While a drawing combination is held, a colored laser dot replaces the normal cursor. Drawing clicks and bound wheel actions are intercepted so they do not click or scroll the application underneath.

## Features

- Transparent, always-on-top drawing overlay across the virtual desktop.
- Freehand and straight-line drawing with a glowing edge.
- Automatic fade and retracting-tail effect.
- Angle snapping near configurable angle increments.
- Quick-pick color palette and custom color picker.
- Independent controls for line/glow width, color, and opacity.
- Configurable keyboard modifiers and mouse bindings.
- Tray icon color follows the selected line color.

## Settings

Use **Apply** to save changes without closing Settings, **OK** to save and close, or **Defaults** to restore the default controls and appearance. **Cancel** discards edits made since the last Apply.

### Appearance and snapping

| Setting | Default |
|---|---|
| Line color | Red (`#FF0000`) |
| Line width | 3 px |
| Line opacity | 75% |
| Glow color | `#F5F5F5` |
| Glow width | 1 px per side |
| Glow opacity | 10% |
| Fade delay | 0.5 seconds |
| Snap angle increment | 30 degrees |
| Snap tolerance | +/-3 degrees |

Snapping applies only to straight-line drawing. For example, with a 30-degree increment, directions near 0, 30, 60, or 90 degrees snap to those angles. Outside the tolerance, you can draw at a free angle. Set the snap angle to **Off** to disable snapping.

The fade delay is the pause before the retracting-tail animation starts, not the total time until a stroke disappears.

### Bindings

Configure each action independently:

- **Freehand** and **Straight line:** any nonempty combination of Ctrl, Alt, Win, and Shift, plus a left, right, or middle mouse drag.
- **Resize** and **Cycle color:** a modifier combination plus the mouse wheel.
- **Straight modifier:** a modifier combination held during freehand drawing, or **Disabled**.

Duplicate action bindings are rejected. Additional held keys are allowed: the matching action with more modifiers wins. Ties favor straight-line drawing over freehand, or color cycling over resizing. Windows or other applications may still reserve some keyboard combinations.

Settings are stored per user at:

```text
%APPDATA%\Pointer\settings.json
```

Existing settings are preserved when upgrading. If you previously customized the activation combination, the app uses it to initialize the per-action bindings.

## Build from source

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The app uses Windows Forms and targets `net10.0-windows`.

Run these commands from the repository root:

```powershell
dotnet restore .\Pointer.csproj
dotnet build .\Pointer.csproj --configuration Release
dotnet run --project .\Pointer.csproj --configuration Release
```

### Publish a single executable

This produces a compressed, self-contained Windows x64 executable with the .NET runtime included:

```powershell
dotnet publish .\Pointer.csproj --configuration Release `
  --runtime win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=embedded `
  --output .\artifacts\Pointer
```

The output is `artifacts\Pointer\Pointer.exe`. The bundled runtime makes it larger than a framework-dependent app, but users do not need to install .NET separately.

## GitHub Actions

The [desktop workflow](.github/workflows/dotnet-desktop.yml):

1. Builds Debug and Release on Windows with the .NET 10 SDK.
2. Publishes and verifies a single compressed executable.
3. Uploads the `Pointer-Release` workflow artifact.
4. After both builds pass on the current main revision, creates or updates the GitHub Release tagged `latest` with `Pointer.exe`.

Pull requests build without publishing a release. Main-branch pushes and manual runs on main can update the rolling download:

```text
https://github.com/alperozcelik/pointer/releases/download/latest/Pointer.exe
```

Release publishing requires GitHub Actions write access to repository contents. There is currently no automated test project or code-signing step in the workflow.
