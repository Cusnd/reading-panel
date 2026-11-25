# Third-Party Notices

Reading Panel uses or packages the following third-party software, libraries, models, and datasets. Each component remains subject to its respective license.

## Dictionary Data & Online Sources

- **ECDICT Enhanced 1.0.28** (`ecdict-sqlite-28.zip`)
  - Source: https://github.com/skywind3000/ECDICT
  - License: MIT License (see [`assets/ECDICT-LICENSE.txt`](assets/ECDICT-LICENSE.txt))
  - Usage: Downloaded and indexed at build time by `tools/build_dictionary.py` for the Offline edition.

- **Chinese Wiktionary** (`zh.wiktionary.org`)
  - Source: https://zh.wiktionary.org/
  - License: Creative Commons Attribution-ShareAlike 4.0 International (CC BY-SA 4.0)
  - Usage: Queried live by the Online edition; each displayed entry links back to its Wiktionary article and license.

## Offline Speech Runtime & Voice Model

Bundled in the Offline distribution (and optionally downloaded by the Online edition into the user data directory) with SHA-256 verification. Full license texts and notices are included in [`assets/Piper-NOTICES.txt`](assets/Piper-NOTICES.txt):

- **Piper 2023.11.14-2** & **Piper Phonemize** — MIT License (https://github.com/rhasspy/piper)
- **ONNX Runtime** — MIT License (https://github.com/microsoft/onnxruntime)
- **eSpeak NG** & language data — GNU General Public License v3.0 or later (https://github.com/espeak-ng/espeak-ng). Invoked by Reading Panel solely as an external subprocess (`piper.exe`).
- **libtashkeel** — MIT License (https://github.com/mush42/libtashkeel)
- **US English John (medium)** voice model by Bryce Beattie — Model card and data provenance included in the downloaded voice pack (`MODEL_CARD`). (https://huggingface.co/rhasspy/piper-voices)

## .NET NuGet Dependencies

- **Google.Cloud.TextToSpeech.V1** — Apache-2.0 License
- **HtmlAgilityPack** — MIT License (see [`assets/HtmlAgilityPack-LICENSE.txt`](assets/HtmlAgilityPack-LICENSE.txt))
- **Microsoft.Data.Sqlite.Core** — MIT License
- **NAudio.WinMM** — MIT License
- **SQLitePCLRaw.bundle_winsqlite3** — Apache-2.0 License
- **System.Security.Cryptography.ProtectedData** — MIT License
- **xUnit** / **Microsoft.NET.Test.Sdk** (test project only) — Apache-2.0 / MIT Licenses
