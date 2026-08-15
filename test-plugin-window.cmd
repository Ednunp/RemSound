@echo off
REM Opens the RemSound PLUGIN window on its own, with no DAW involved, so its controls can be
REM tested with NVDA before any plugin plumbing is built. Double-click this file.
REM Escape or the Close button shuts it. Nothing is saved and no audio is touched.
start "" "%~dp0RemSound.exe" --plugin-window
