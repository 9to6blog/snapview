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

Escape coverage includes every focused toolbar button after capture selection,
an idle capture overlay, a selected recording area, and an unmatched release
followed by a full press. Assertions verify preview-event consumption, waiting
through repeated keydown until release, cancellation with no capture result,
`DialogResult == false`, exactly one close, handling the release before closing,
and no focused-button click.

The Escape regression was reproduced against the previous handler on 2026-09-22:
**184 passed, 62 failed**, including all **103 existing assertions passing**.

Verified after the fix on 2026-09-22: **246 passed, 0 failed**.

This is WPF event-routing integration coverage. It does not inject physical OS
keyboard/mouse input or verify the creation of a capture image on disk.
