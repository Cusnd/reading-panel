# Reading Panel

[![CI](https://github.com/Cusnd/reading-panel/actions/workflows/ci.yml/badge.svg)](https://github.com/Cusnd/reading-panel/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Cusnd/reading-panel)](https://github.com/Cusnd/reading-panel/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D6)](#run-on-windows)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](#build-and-test)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

English | [简体中文](README.zh-CN.md)

Highlight English text and middle-click in the same reader window to see its Chinese meaning without leaving your line or losing window focus. The **Online** edition fetches definitions from Chinese Wiktionary on demand. The **Offline** edition bundles the full 3.4M-entry ECDICT index and the offline Piper John voice. Both editions support streaming English pronunciation with Google Chirp 3 HD or OpenAI Realtime, and sentence translation via DeepL, DeepSeek, or Google Cloud Translation.

## Key features

- **Non-activating middle-click popup**: Highlight a word, phrase, or sentence in any Windows reader and middle-click once. Your reading window keeps focus; clicking or scrolling back into the document dismisses the card and delivers input on the first try.
- **Two editions, shared settings**: Lightweight **Online** edition (Chinese Wiktionary, no API key required) and self-contained-dictionary **Offline** edition (3,402,564 ECDICT Enhanced entries with exam tags like CET-4, CET-6, IELTS, GRE, plus collapsible base-word/lemma senses).
- **Sentence translation with instant switching**: Supports **DeepL**, **DeepSeek**, and **Google Cloud Translation**. Switch providers directly under a translation to re-run the selection, or test credentials directly in Settings.
- **Streaming English pronunciation**: Auto-routing across **Google Chirp 3 HD (Charon)**, **OpenAI Realtime (cedar)**, and local **Piper (John)** with in-memory replay cache, settings voice audition, monthly quota tracking, and optional BigQuery billing export reconciliation.
- **System-wide desktop & keyboard support**: Works across browsers, PDF readers, editors (including Zed and terminals via `Ctrl+Insert`), and chat apps (including WeChat), plus customizable global hotkeys (`Ctrl+Alt+L` to look up, `Ctrl+Alt+M` to toggle middle-click), launch-at-login, and DPAPI-encrypted local settings.

## Run on Windows

Download the latest release from [GitHub Releases](https://github.com/Cusnd/reading-panel/releases/latest).

| Windows x64 edition | Word and phrase lookup |
| --- | --- |
| **Online** · `ReadingPanel-win-x64-global-online.zip` | Reads English entries from Chinese Wiktionary over the internet. Word lookups use its public API without an account or key. Each result links to the article and its CC BY-SA 4.0 license. |
| **Offline** · `ReadingPanel-win-x64-global-offline.zip` | Includes all 3,402,564 entries from ECDICT Enhanced 1.0.28 (简明英汉字典增强版) and the offline Piper John voice. Words and phrases work completely offline. Shows exam tags (CET-4, CET-6, IELTS, GRE, etc.) when present. |

1. Extract the entire ZIP and open `ReadingPanel.exe` from the extracted folder. Both editions require the **[.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)**.
   - The Offline ZIP uses PPMd compression to keep the dictionary small. Extract it with [7-Zip](https://www.7-zip.org/) or run `tar -xf ReadingPanel-win-x64-global-offline.zip` in Windows Terminal / PowerShell.
2. With Reading Panel running in the system tray, highlight text and middle-click once in the foreground reader window. The card opens at the pointer or beside the selected line when Windows supplies its position. Click the speaker button to hear the original selection.
3. Click or scroll back in the reader, press `Escape`, or switch windows to dismiss the card. The reader receives its usual middle click (opening links or starting autoscroll) when the selection is empty or cannot be confirmed.

### Lookup, translation, and settings

- **Base-word (lemma) disclosure (Offline)**: When the dictionary links an inflected form to its base word (for example, *building* → *build*, or *circumstances* → *circumstance*), click **原形 …** to expand or collapse base meanings. Choose **默认收起** or **默认展开** under **Settings → 查词 → 原形释义**.
- **Sentence translation**: Under **Settings → 翻译**, enable or disable sentence translation, pick a provider, and use **测试连接** to verify credentials. Choosing another provider beneath a translation card re-runs the selection and saves the choice.
  - **DeepL** (default): Automatically selects the Free (`:fx`) or Pro endpoint from your API key. [DeepL authentication](https://developers.deepl.com/docs/getting-started/auth)
  - **DeepSeek**: Accepts an API key and model name (defaults to `deepseek-flash`). [DeepSeek API](https://api-docs.deepseek.com/)
  - **Google Cloud**: Enable the Cloud Translation API in your Google Cloud project. Leave the API key blank to reuse Application Default Credentials (ADC) and the optional quota project configured for Google pronunciation, or enter a Cloud Translation API key. [Cloud Translation setup](https://docs.cloud.google.com/translate/docs/setup)
- **Shortcuts & general preferences**:
  - `Ctrl+Alt+M` toggles middle-click lookup on or off; `Ctrl+Alt+L` looks up the focused selection even when middle-click lookup is off. Both shortcuts can be recorded, cleared, or checked for conflicts under **Settings → 查词**. The keyboard card supports `Tab`, `Enter`, and `Escape`.
  - Under **Settings → 常规**, enable **开机自动启动** (launch silently to the tray at Windows sign-in), toggle middle-click status balloon notifications, open the local data directory, or reset preferences to defaults.
- **How desktop selection works**: Drag across text or double-click a word in a desktop app (including WeChat message bubbles), then middle-click or press `Ctrl+Alt+L`. Reading Panel asks the foreground app to copy with `Ctrl+C`, reads the new text, and restores the previous clipboard formats. Clipboard history managers may observe the temporary copy. Windows UI Automation also supplies keyboard selections and fallback reads. Zed and supported terminal windows use `Ctrl+Insert` (Zed's copy metadata distinguishes real selections from whole-line caret copies). Password fields are excluded. Scanned PDFs require a selectable text layer. [Zed copy behavior](https://github.com/zed-industries/zed/blob/main/crates/editor/src/clipboard.rs)
- **Switching editions or upgrading**: Choose **退出** (Exit) from the tray icon and launch the other executable. Both editions share settings. The desktop reader works without the optional Chromium extension under [`extension/`](extension/).

## English pronunciation

Click the speaker button on a card to start or stop reading the original selection. Right-click it to choose **自动**, **Google · Charon**, **OpenAI · cedar**, or **Piper · John**, or open **用量与朗读设置** to inspect monthly usage and audition voices (**试听**). Audio streams as it arrives, and completed short recordings are cached in memory for instant replay.

| Voice | Setup |
| --- | --- |
| **Google Chirp 3 HD · Charon** | Enable Cloud Text-to-Speech and billing in your Google Cloud project. Authenticate via Application Default Credentials (e.g. `gcloud auth application-default login`) and optionally set a quota project in Settings. [Google ADC](https://cloud.google.com/docs/authentication/provide-credentials-adc) |
| **OpenAI Realtime · cedar** | Enter an OpenAI API key in Settings or set the `OPENAI_API_KEY` environment variable. Uses `gpt-realtime-2.1-mini` and bills text and audio tokens. [Model & pricing](https://developers.openai.com/api/docs/models/gpt-realtime-2.1-mini) |
| **Piper · John (offline)** | Bundled in the Offline edition (US English medium model + Windows x64 runtime). Online offers a one-click download in Settings with SHA-256 verification. Speech synthesis runs entirely on-device. |

- **Automatic routing**: Starts with Google Chirp 3 HD. When a request would leave 50,000 characters or fewer of the 1,000,000-character monthly allowance, it switches to OpenAI. If the chosen cloud voice fails before producing audio, automatic mode falls back to an installed Piper voice. Manual cloud choices report errors explicitly. [Chirp 3 HD pricing](https://cloud.google.com/text-to-speech/pricing)
- **Usage & billing export**: The local ledger tracks monthly usage in Pacific time. To reconcile account-wide Google usage across devices and projects, enter your fully qualified **standard Cloud Billing export table** (`project.dataset.table`) under **Google 凭据与账户账单**. Reading Panel queries the Chirp 3 HD SKU on demand and up to hourly. Without an export table, the panel clearly labels counts as local estimates. [Billing export schema](https://cloud.google.com/billing/docs/how-to/export-data-bigquery-tables/standard-usage)

## Privacy and credentials

- Reading Panel collects **no telemetry or analytics**.
- API keys, provider choices, and preferences are encrypted per Windows user via **DPAPI** (`ProtectedData`) in `%LOCALAPPDATA%\ReadingPanel\settings.dat`.
- Offline word lookups stay strictly on your machine. Network requests are sent only to Chinese Wiktionary (Online edition) or to the translation / speech services you explicitly configure and invoke.

## Build and test

Prerequisites on Windows x64:

- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) and PowerShell
- For Offline dictionary indexing and packaging: **Python 3** and **[7-Zip](https://www.7-zip.org/)**

```powershell
# Build both Online and Offline ZIP packages into dist/
.\tools\publish.ps1 -Edition Both

# Run Offline and Online unit test suites
dotnet test tests/ReadingPanel.Tests/ReadingPanel.Tests.csproj
dotnet test tests/ReadingPanel.Tests/ReadingPanel.Tests.csproj -p:OnlineEdition=true
```

- Pass `-Edition Online` or `-Edition Offline` to package a single edition, and `-Python <path-to-python.exe>` to pick a specific interpreter.
- Running `.\tools\publish.ps1` without arguments builds a self-contained Offline ZIP (with the .NET runtime embedded); `-Compact` builds a framework-dependent Offline ZIP.
- When `assets/ecdict.sqlite3` is missing or outdated, `tools/build_dictionary.py` downloads [`ecdict-sqlite-28.zip`](https://github.com/skywind3000/ECDICT/releases/tag/1.0.28), verifies its SHA-256 digest, and builds the indexed SQLite database. You can also supply a local archive:
  ```powershell
  python tools/build_dictionary.py --archive <path-to-ecdict-sqlite-28.zip>
  ```
- For local development:
  ```powershell
  dotnet run --project src/ReadingPanel                         # Offline (after building index)
  dotnet run --project src/ReadingPanel -p:OnlineEdition=true   # Online
  ```
- Optional browser adapter regression tests (`tests/browser_gestures.py`) run with `python tests/browser_gestures.py` in a Python environment with Playwright and Chromium installed.

## Extend it

| To add | Implement |
| --- | --- |
| A translation service | Implement `ITranslationProvider` in [`Translation.cs`](src/ReadingPanel/Core/Translation.cs) and register it in [`TrayHost.cs`](src/ReadingPanel/TrayHost.cs). The card and settings screen discover it automatically. A local service can set `RequiresApiKey => false`. |
| A reader adapter | Apps exposing selected text and screen bounds through UI Automation work with [`WindowsReader.cs`](src/ReadingPanel/Integration/WindowsReader.cs) directly. Custom adapters can pass a `Selection` ([`Lookup.cs`](src/ReadingPanel/Core/Lookup.cs)) to `TrayHost.OpenSelection` on the UI dispatcher. |
| A dictionary | Implement `IWordLookup.FindAsync` or the synchronous `IWordDictionary.Find` adapter in [`WordLookup.cs`](src/ReadingPanel/Core/WordLookup.cs) and wire it into `TrayHost`. |

See [DESIGN.md](DESIGN.md) for the interaction model and acceptance criteria, and [CONTRIBUTING.md](CONTRIBUTING.md) for contribution guidelines.

## License

Reading Panel is released under the [MIT License](LICENSE).

Third-party libraries, dictionary data, and voice runtimes are governed by their respective licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and the files under [`assets/`](assets/).
