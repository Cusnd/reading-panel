# Contributing to Reading Panel

Thank you for your interest in contributing to Reading Panel! Issues and pull requests are welcome in either English or Chinese.

## Development Setup

Reading Panel targets **Windows x64** with **.NET 8** (`net8.0-windows`, WPF + WinForms tray host).

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).
2. For working on the **Offline** edition or running Offline dictionary tests, install **Python 3** and build the local ECDICT SQLite index:
   ```powershell
   python tools/build_dictionary.py
   ```
3. For packaging Offline release archives (`tools/publish.ps1 -Edition Offline`), install [7-Zip](https://www.7-zip.org/).

## Running & Testing

Run both edition test suites before opening a pull request:

```powershell
# Offline edition tests (requires assets/ecdict.sqlite3)
dotnet test tests/ReadingPanel.Tests/ReadingPanel.Tests.csproj

# Online edition tests (Wiktionary parser & controller)
dotnet test tests/ReadingPanel.Tests/ReadingPanel.Tests.csproj -p:OnlineEdition=true
```

To run the application locally:

```powershell
dotnet run --project src/ReadingPanel                         # Offline edition
dotnet run --project src/ReadingPanel -p:OnlineEdition=true   # Online edition
```

If you modify files under `extension/`, also run the Playwright browser gesture tests:

```powershell
python tests/browser_gestures.py
```

## Guidelines

- **Keep changes focused**: Follow the existing architecture (`Core/` for platform-independent lookup, translation, settings, and usage logic; `Integration/` for Win32 hooks, clipboard, UI Automation, speech, and native messaging; `UI/` for WPF presentation).
- **Preserve non-activating focus behavior**: Avoid introducing changes to `PanelWindow` that steal focus from the foreground reading window during mouse-triggered lookups.
- **Do not commit secrets or generated binaries**: Never commit API keys, `settings.dat`, `assets/*.sqlite3`, or `dist/` build outputs.
- **Add tests for behavior changes**: Include xUnit coverage in `tests/ReadingPanel.Tests/` for both Offline and Online paths where applicable.

## Pull Request Process

1. Fork the repository and create a branch from `main`.
2. Make your changes and verify both Offline and Online test suites pass.
3. Open a pull request describing what changed, why, and how it was verified.
