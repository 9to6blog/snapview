# Capture overlay keyboard regression

Run from the repository root:

```powershell
dotnet run --project tests/OverlayKeys/OverlayKeys.csproj --configuration Release
```

This focused WPF test opens synthetic capture overlays and routes preview/bubbling
key events through the actual focused toolbar controls. It uses the application's
real styles, without starting its tray controller or altering user settings,
clipboard, or capture files.

Coverage includes all six toolbar buttons, magnet action followed by Space,
repeated keydown, save on keyup, no button reactivation, unmatched key release,
empty selection, recording confirmation, and Enter confirmation.

Verified on 2026-09-22: **103 passed, 0 failed**.

This is WPF event-routing integration coverage. It does not inject physical OS
keyboard/mouse input or verify the creation of a capture image on disk.
