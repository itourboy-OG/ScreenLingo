<p align="center">
  <img src="assets/logo.png" alt="ScreenLingo logo" width="72" />
</p>

<h1 align="center">ScreenLingo</h1>
<p align="center"><strong>Understand what's on screen.</strong><br />A compact Windows translator for game menus and everyday applications.</p>
<p align="center">
  <a href="https://github.com/itourboy-OG/ScreenLingo/releases/latest"><strong>Download for Windows</strong></a> ·
  <a href="https://github.com/itourboy-OG/ScreenLingo/releases">Release notes</a> ·
  <a href="https://github.com/itourboy-OG/ScreenLingo/issues">Report an issue</a>
</p>

Read a game's settings, navigate an unfamiliar menu, or translate another application without reaching for your phone. Choose your output language, switch to the window you want to read, and toggle the translation overlay with **Ctrl+Alt+T**.

**Early Windows preview.** Actual games and exclusive fullscreen still need live testing. See [compatibility and current limits](#compatibility-and-current-limits) before choosing a display mode.

## A look inside

<table>
  <tr><th>Language controls</th><th>Settings</th></tr>
  <tr>
    <td align="center"><img src="assets/screenlingo.png" alt="ScreenLingo in the Copper skin, with automatic source detection, Spanish output and a translation toggle" width="280" /></td>
    <td align="center"><img src="assets/settings.png" alt="ScreenLingo Settings showing appearance, translation service, capture scope and accessibility controls" width="300" /></td>
  </tr>
</table>

<details>
<summary><strong>Three skins: Copper, Paper and Plum</strong></summary>

Choose a look in **Settings → Appearance**. The controls and translation labels follow your saved skin.

<table>
  <tr><th>Copper</th><th>Paper</th><th>Plum</th></tr>
  <tr>
    <td align="center"><img src="assets/screenlingo.png" alt="Copper skin with warm charcoal surfaces and peach accents" width="210" /></td>
    <td align="center"><img src="assets/paper.png" alt="Paper skin with light cream surfaces and rust accents" width="210" /></td>
    <td align="center"><img src="assets/plum.png" alt="Plum skin with dark violet surfaces and lilac accents" width="210" /></td>
  </tr>
</table>

</details>

## Get started

**Requires Windows 10 version 2004 or newer, or Windows 11, x64.** The Windows package includes .NET. If native text recognition cannot start because a runtime is missing, install the [Microsoft Visual C++ x64 runtime](https://aka.ms/vs/17/release/vc_redist.x64.exe).

1. Open the [latest release](https://github.com/itourboy-OG/ScreenLingo/releases/latest) and download the ZIP ending in **`win-x64.zip`**. The source ZIP is for development.
2. Extract the **entire ZIP** to a folder and open **`ScreenLingo.exe`**. Keep its supporting files beside it.
3. Set **From** to automatic detection or the language you know is on screen. Choose your language under **Translate to**.
4. Enable translation, switch to your game or application, and open a menu. Start with a windowed or borderless display mode.
5. Press **Ctrl+Alt+T** to pause or resume whenever you need it.

For a first test, try an English game's settings menu with **English → Spanish**. If automatic detection misreads a short or mixed-language menu, choose the source language manually.

| Shortcut | Action |
| --- | --- |
| **Ctrl+Alt+T** | Enable or pause translation |
| **Ctrl+Alt+H** | Open the control window |
| **Ctrl+Alt+Esc** | Pause translation |

Assign Ctrl+Alt+T to a **Stream Deck** hotkey button. Minimize the control window to keep ScreenLingo in the system tray. Close the window or use the tray's Quit command to exit.

## What you can control

| Feature | Available in the preview |
| --- | --- |
| Languages | Automatic or manual source selection; English, Spanish, Simplified Chinese, French, German, Italian, Japanese, Korean, Portuguese and Russian |
| Capture | The active window, the window under your pointer, or the monitor under your pointer |
| Translation | Online with MyMemory, or locally with a separately installed Ollama model |
| Appearance | Copper, Paper and Plum skins; text size and background opacity |
| Accessibility | Keyboard navigation, reduced motion, Windows high contrast and an overlay that lets clicks pass through |

Text already in your output language skips the translation request while ScreenLingo continues watching for changes. Unchanged frames skip recognition, and previously translated labels are cached for the session. A refresh interval sets the scan schedule; recognition and translation can take longer than that interval.

## Translation services and privacy

**Online · MyMemory.** No API key is needed. Recognized text is sent to MyMemory; screenshots remain on your PC. Anonymous usage is limited to **5,000 characters per day** by the provider. Translation accuracy, response time and preservation of menu lines depend on the service. An exhausted quota or malformed response stops translation with a specific error. ScreenLingo does not silently switch services. See [usage limits](https://mymemory.translated.net/doc/usagelimits.php) and [terms](https://mymemory.translated.net/doc/en/tos.php).

**Offline · Ollama.** Requires a separate runtime and a downloaded multilingual model. ScreenLingo sends recognized text to **`127.0.0.1:11434`**. After installing the runtime and model, translation can work without internet. Offline setup and performance still need live validation.

<details>
<summary><strong>Set up local translation</strong></summary>

Install [Ollama for Windows](https://ollama.com/download/windows), then download the model used by the initial setting:

```powershell
ollama pull qwen3:4b
```

The [qwen3:4b model](https://ollama.com/library/qwen3:4b) download is approximately 2.5 GB. Select **Offline · Ollama** in Settings and enter the exact installed model name. Keep Ollama running while you translate.

</details>

Preferences and retry warnings are stored under **`%LOCALAPPDATA%\ScreenLingo`**. Diagnostic logs contain service hosts and retry codes, not screenshots or recognized text. ScreenLingo's control window and overlay are excluded from its capture.

## Updates

ScreenLingo checks GitHub Releases **on startup and every six hours** while running. You can also use **Settings → Check for updates**.

When a newer Windows package is available, the main screen shows an update banner. Download it with progress, then choose **Install now** to close, update and reopen the app, or **Install later** to keep the download across restarts. Your preferences are preserved.

The updater verifies the package size and GitHub SHA-256 digest, rejects unsafe ZIP paths, and checks staged files before installation. Downloads and backups of overwritten files stay under **`%LOCALAPPDATA%\ScreenLingo\Updates`**. Installation errors are shown explicitly, and previous files are restored if copying fails. Update checks do not send screenshots or recognized text to GitHub.

## Compatibility and current limits

- **Game compatibility is still being tested.** Capture and overlays were checked with test windows, including a borderless window. Call of Duty Mobile, GTA and other actual games still require live tests.
- **Exclusive fullscreen is unverified.** A separate desktop overlay may not appear over an exclusive fullscreen game. Protected content or game restrictions can block capture. ScreenLingo does not inject into games or bypass those restrictions.
- **Recognition and translation can be imperfect.** Short labels, mixed languages and game-specific terms can be ambiguous. ScreenLingo translates visible text; it does not change the game's own language setting.
- **Offline translation is unverified.** The connector is included, but its setup, speed and accuracy still need a real local-model test.

Real integration checks cover window capture, English/Chinese recognition, language detection, online translation, overlay exclusion, skin persistence, public update downloads and native update installation. Synthetic menus were used for translation checks.

## Help shape the next update

The next focus is testing real game menus in **English → Spanish** and **Chinese → English/Spanish**, checking speed and display-mode behavior, and validating local translation. These are testing priorities, not promised release dates or confirmed compatibility claims.

[Report a problem](https://github.com/itourboy-OG/ScreenLingo/issues) with the game or application name, source and output languages, windowed/borderless/fullscreen mode, and what happened. Released additions, changes and fixes are listed in the [release notes](https://github.com/itourboy-OG/ScreenLingo/releases).

<details>
<summary><strong>Build and check from source</strong></summary>

Use the **.NET 9 SDK on Windows**. The source includes the recognition models and locked dependency versions.

```powershell
dotnet restore ScreenLingo.csproj --locked-mode --configfile NuGet.Config
dotnet build ScreenLingo.csproj --no-restore
dotnet run --project ScreenLingo.csproj --no-build -- --smoke-test C:\path\to\test-output
dotnet run --project ScreenLingo.csproj --no-build -- --install-check C:\path\to\install-test-output C:\path\to\package.zip
dotnet run --project ScreenLingo.csproj --no-build -- --update-check C:\path\to\update-test-output
.\Publish.ps1 -PackageDirectory C:\path\to\package
```

The smoke check creates synthetic menu windows, captures those windows, calls the real online service with synthetic menu text, and writes screenshots plus `smoke-report.json`. It never sends your desktop contents to a translation service.

`Publish.ps1` builds the self-contained Windows package, runs the appearance check and refreshes `Desktop\ScreenLingo\ScreenLingo.exe` after the check passes. Close the Desktop copy first. The appearance check uses the actual preferences form, verifies theme persistence and renders each skin without network requests. Results are saved in `screen-lingo-appearance-check` beside the package directory.

</details>

Third-party licenses are included in `Licenses` and summarized in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
