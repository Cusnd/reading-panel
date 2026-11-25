# Reading Panel

[![CI](https://github.com/Cusnd/reading-panel/actions/workflows/ci.yml/badge.svg)](https://github.com/Cusnd/reading-panel/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Cusnd/reading-panel)](https://github.com/Cusnd/reading-panel/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D6)](#在-windows-上运行)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](#构建与测试)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

[English](README.md) | 简体中文

面向 Windows 的划词中键查词、句子翻译与英文朗读面板。在阅读窗口中选中英文后单击鼠标中键，即可在不打断阅读、不抢占窗口焦点的前提下弹出简洁释义卡片。**Online 版**按需从中文维基词典获取释义；**Offline 版**内置 340 万条 ECDICT 增强词典与离线 Piper John 语音。两个版本均支持 Google Chirp 3 HD / OpenAI Realtime 流式英文朗读，以及 DeepL、DeepSeek 与 Google Cloud 句子翻译。

## 核心特性

- **不抢焦点的中键浮窗**：在任意 Windows 阅读软件中选中单词、短语或句子后按一次鼠标中键，释义卡在光标或选区旁弹出；继续点击或滚动原文档会直接关闭卡片并将输入送达文档。
- **双版本、共享配置**：轻量 **Online 版**（中文维基词典，免 Key 即用）与完整 **Offline 版**（内置 3,402,564 条 ECDICT 增强词条，带 CET-4/CET-6/IELTS/GRE 等考试标签，支持词形变化与原形释义展开/收起）。
- **多引擎句子翻译与即时切换**：内置 **DeepL**、**DeepSeek** 和 **Google Cloud Translation**。在翻译卡片下方可直接切换服务重新翻译并记住偏好，设置页支持一键**测试连接**。
- **流式英文朗读与用量统计**：支持 **Google Chirp 3 HD (Charon)**、**OpenAI Realtime (cedar)** 与本地离线 **Piper (John)**，自动模式支持额度预警切换与离线兜底；内置内存重播缓存、设置页试听与可选 BigQuery 账单对账。
- **全桌面与键盘工作流**：兼容浏览器、PDF 阅读器、编辑器（含 Zed 与支持 `Ctrl+Insert` 的终端）及聊天软件（含微信气泡）；支持自定义全局快捷键（默认 `Ctrl+Alt+L` 键盘查词、`Ctrl+Alt+M` 切换中键查词）、开机静默自启，以及 Windows DPAPI 本地加密凭据存储。

## 在 Windows 上运行

前往 [GitHub Releases](https://github.com/Cusnd/reading-panel/releases/latest) 下载最新版本压缩包：

| Windows x64 版本 | 查词与语音 |
| --- | --- |
| **Online** · `ReadingPanel-win-x64-global-online.zip` | 通过中文维基词典公开 API 按需查询英语词条与 3 词以内短语，无需账号或密钥，结果附带词条原文与 CC BY-SA 4.0 协议链接。 |
| **Offline** · `ReadingPanel-win-x64-global-offline.zip` | 内置 ECDICT Enhanced 1.0.28（简明英汉字典增强版）全部 3,402,564 个词条与离线 Piper John 语音包，完全离线可用，支持考试标签与短语查询。 |

1. 完整解压 ZIP 后运行其中的 `ReadingPanel.exe`。两个版本均需安装 **[.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)**。
   - Offline 压缩包使用 PPMd 压缩算法以减小词典体积，请使用 [7-Zip](https://www.7-zip.org/) 解压，或在下载目录执行 `tar -xf ReadingPanel-win-x64-global-offline.zip`。
2. Reading Panel 驻留系统托盘后，在阅读窗口选中文本并单击中键即可呼出卡片；点击扬声器图标朗读所选英文。
3. 在原阅读窗口点击、滚动、按 `Escape` 或切换窗口即可关闭卡片。无选区或无法确认选区时，中键会原样交给原程序（如中键打开链接或自动滚屏）。

### 查词、翻译与设置

- **原形释义（Offline）**：当词典将屈折变化关联到词元（例如 *building* → *build*、*circumstances* → *circumstance*）时，点击 **原形 …** 可展开或收起原形释义，也可在 **设置 → 查词 → 原形释义** 中设为 **默认收起** 或 **默认展开**。
- **句子翻译**：在 **设置 → 翻译** 中可开关句子翻译、选择服务商并点击 **测试连接** 验证配置。在卡片底部切换服务会立即用新服务重新翻译当前选区并保存选择：
  - **DeepL**（默认）：根据 API key 自动选择 Free（`:fx`）或 Pro 接口。[DeepL 认证说明](https://developers.deepl.com/docs/getting-started/auth)
  - **DeepSeek**：填写 API key 与模型名（默认 `deepseek-flash`）。[DeepSeek API 文档](https://api-docs.deepseek.com/)
  - **Google Cloud**：在 Google Cloud 项目中启用 Cloud Translation API。密钥留空时自动复用朗读配置中的 Google 应用默认凭据（ADC）和配额项目，也可单独填入 API key。[Cloud Translation 配置](https://docs.cloud.google.com/translate/docs/setup)
- **快捷键与常规设置**：
  - `Ctrl+Alt+M` 开关中键查词；`Ctrl+Alt+L` 在关闭中键查词时也能对当前焦点选区进行键盘查词（键盘卡片支持 `Tab`、`Enter`、`Escape`）。在 **设置 → 查词** 中可直接按键录制、清除快捷键并查看注册冲突状态。
  - 在 **设置 → 常规** 中可开启 **开机自动启动**（登录 Windows 时静默启动到托盘）、开关中键切换气泡提示、打开本地数据目录或一键恢复默认偏好。
- **桌面取词机制**：在任意桌面软件（含微信消息气泡）中拖选或双击文字后按中键或 `Ctrl+Alt+L`，程序会向当前窗口发送 `Ctrl+C` 读取选区并立即恢复原有剪贴板格式（剪贴板历史工具可能会观察到这次临时复制）；键盘选区及不可复制场景会回退到 Windows UI Automation；Zed 与支持的终端使用 `Ctrl+Insert`（并借助 Zed 复制元数据区分真实选区与空光标整行复制）。密码框会被自动排除；扫描版 PDF 需具备可选文字层。
- **切换版本或升级**：右键系统托盘图标选择 **退出** 后启动另一版本即可，两版本共享本地配置。桌面查词不依赖浏览器扩展；[`extension/`](extension/) 为可选的 Chromium 扩展桥接实现。

## 英文朗读

点击卡片上的扬声器按钮开始或停止朗读原选区；右键扬声器可切换 **自动**、**Google · Charon**、**OpenAI · cedar** 或 **Piper · John**，也可打开 **用量与朗读设置** 查看本月用量或点击 **试听**。音频边到达边播放，已完成的短音频会缓存在内存中供即时重播。

| 语音引擎 | 配置方式 |
| --- | --- |
| **Google Chirp 3 HD · Charon** | 在 Google Cloud 项目中启用 Cloud Text-to-Speech 并关联结算账号，通过应用默认凭据登录（如 `gcloud auth application-default login`），必要时在设置中填写配额项目。[Google ADC 文档](https://cloud.google.com/docs/authentication/provide-credentials-adc) |
| **OpenAI Realtime · cedar** | 在设置中填写 OpenAI API key，或配置环境变量 `OPENAI_API_KEY`。使用 `gpt-realtime-2.1-mini` 模型，按文本与音频 token 计费。[模型与价格](https://developers.openai.com/api/docs/models/gpt-realtime-2.1-mini) |
| **Piper · John（离线）** | Offline 版直接内置美式英语 medium 模型与 x64 运行时；Online 版可在设置中一键下载（带 SHA-256 校验），语音合成完全在本地完成。 |

- **自动路由策略**：优先使用 Google Chirp 3 HD；当本月 1,000,000 字符额度预计剩余不超过 50,000 字符时自动切换至 OpenAI；当云端服务在产出音频前不可用时，自动模式会回退至已安装的本地 Piper 语音，手动指定云端语音则会明确提示错误。
- **月度账本与账单对账**：本地账本按太平洋时间按月记录用量。如需跨设备汇总整个 Google 账单账户的真实用量，可在 **Google 凭据与账户账单** 中填写标准 Cloud Billing 导出表（`project.dataset.table`），程序会按需及每小时查询 Chirp 3 HD SKU 并估算待核字符数。未配置导出表时界面会明确标注为本机估算值。

## 隐私与安全

- **零遥测**：不收集任何使用统计、埋点或崩溃上报数据。
- **本地加密凭据**：API key、服务商偏好与设置使用 Windows **DPAPI**（`ProtectedData`，仅限当前 Windows 用户解密）加密保存在 `%LOCALAPPDATA%\ReadingPanel\settings.dat`。
- **最小网络传输**：Offline 版查词完全在本地 SQLite 中完成；仅在 Online 查词、启用句子翻译或使用云端朗读时向你显式选择的服务发送所选文本。

## 构建与测试

Windows x64 开发环境要求：

- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) 与 PowerShell
- 构建离线词典索引及打包 Offline 版需要 **Python 3** 与 **[7-Zip](https://www.7-zip.org/)**

```powershell
# 同时构建 Online 与 Offline 发布包到 dist/
.\tools\publish.ps1 -Edition Both

# 运行 Offline 与 Online 单元测试
dotnet test tests/ReadingPanel.Tests/ReadingPanel.Tests.csproj
dotnet test tests/ReadingPanel.Tests/ReadingPanel.Tests.csproj -p:OnlineEdition=true
```

- 可使用 `-Edition Online` 或 `-Edition Offline` 单独打包；使用 `-Python <python.exe 路径>` 指定 Python 解释器。
- 当 `assets/ecdict.sqlite3` 缺失或版本过期时，`tools/build_dictionary.py` 会自动下载 [`ecdict-sqlite-28.zip`](https://github.com/skywind3000/ECDICT/releases/tag/1.0.28)、校验 SHA-256 并生成离线索引；也可通过 `--archive <本地压缩包路径>` 指定已有文件。
- 开发调试运行：
  ```powershell
  dotnet run --project src/ReadingPanel                         # Offline 版（需先生成离线索引）
  dotnet run --project src/ReadingPanel -p:OnlineEdition=true   # Online 版
  ```
- 可选浏览器扩展回归测试（`tests/browser_gestures.py`）需在安装 Playwright 与 Chromium 的 Python 环境中执行 `python tests/browser_gestures.py`。

## 二次扩展

| 扩展目标 | 实现方式 |
| --- | --- |
| 新增翻译服务 | 在 [`Translation.cs`](src/ReadingPanel/Core/Translation.cs) 中实现 `ITranslationProvider` 并在 [`TrayHost.cs`](src/ReadingPanel/TrayHost.cs) 注册，卡片与设置界面会自动发现；本地服务可将 `RequiresApiKey` 设为 `false`。 |
| 适配阅读器 | 支持 Windows UI Automation 选区与边界的应用可直接通过 [`WindowsReader.cs`](src/ReadingPanel/Integration/WindowsReader.cs) 工作；自定义适配器可在 UI 调度线程调用 `TrayHost.OpenSelection` 传入 `Selection`。 |
| 替换词典源 | 实现 [`WordLookup.cs`](src/ReadingPanel/Core/WordLookup.cs) 中的 `IWordLookup.FindAsync` 或 `IWordDictionary.Find`，并在 `TrayHost` 中注入。 |

更多交互细节与验收标准见 [DESIGN.md](DESIGN.md)，贡献流程见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 开源协议

本项目基于 [MIT License](LICENSE) 开源。

第三方库、词典数据与离线语音组件的版权与协议归属见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 及 [`assets/`](assets/) 目录中的对应声明文件。
