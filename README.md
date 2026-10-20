# AI Cloud Proxy (AICloudProxy.exe)

<p align="center">
  <img alt="Version" src="https://img.shields.io/badge/version-1.0.0-009E9F">
  <img alt="License" src="https://img.shields.io/github/license/local1907/AiCloudProxy">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows-0078D6">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-512BD4">
</p>

A Windows desktop app (C# / .NET 10 / WPF) that exposes an **Ollama-compatible HTTP endpoint**
on your machine and forwards every request to a cloud AI provider (**DeepSeek**, **OpenAI**,
**Gemini**, **Claude**, or **OpenRouter**). Tools that normally talk to Ollama (VS Code extensions, Continue,
Cline, etc.) can be pointed at `http://127.0.0.1:<port>` and will work against your chosen
cloud provider — no local model downloads needed.

> 🔒 **Your API keys never leave your machine.** Keys are stored only in
> `%APPDATA%\AiCloudProxy\settings.json` and are never committed to this repository.

## Screenshots

![Main window](web/assets/screenshots/main-window.png)
*Main window — pick a provider (Gemini shown), paste your API key, and Start the proxy.*

![Test tab](web/assets/screenshots/test-tab.png)
*Test tab — stream a live answer directly from the selected provider.*

![Options & log](web/assets/screenshots/log-options.png)
*Options, feedback and the live log — active providers at a glance.*

![Quick Tour](web/assets/screenshots/quick-tour.png)
*Quick Tour — a 30-second guided setup shown on first launch.*

> Screenshots live in `web/assets/screenshots/` and are shared with the
> [AICloudProxy.com marketing site](web/).

## Features

- **Desktop GUI** — enter your API key, set the port, choose a provider, Start/Stop the proxy, and watch a live log.
- **System tray** — keeps running silently next to the clock; reopen or exit from the tray icon.
- **In-process HTTP listener** — a lightweight ASP.NET Core service hosted inside the app:
  - `POST /api/chat` — Ollama chat format (NDJSON streaming)
  - `POST /api/generate` — Ollama generate format (NDJSON streaming)
  - `POST /api/show`, `GET /api/show` — per-model metadata + capabilities (required by Visual Studio 2026 Copilot BYOM)
  - `POST /v1/chat/completions` — OpenAI format (SSE streaming)
  - `GET /api/tags` — advertises all discovered models (with capabilities & token limits) so tools can discover them
  - `GET /v1/models` — OpenAI-compatible model list
- **Configurable port** and **configurable API key**.
- **Per-provider settings** — every AI provider keeps its own API key, base URL and last model; switching providers restores them automatically.
- **Custom AI provider** — add your own OpenAI-compatible endpoint (name + base URL + key + model) and it appears in the provider dropdown.
- **Test tab** — ask a question and stream the answer directly from the selected provider.
- **Copyable log** — "Copy" puts all log lines on the clipboard (the proxy log is otherwise read-only).
- **Model auto-discovery** — "Get Models" fetches the provider's live model list; on Start the configured model is validated against it and you're warned (switch / continue / cancel) if it no longer exists.
- **Editable model filter** — choose which models get advertised by editing a keyword list in the **Options** tab; for example un-hide music/sound-effect/audio/TTS/image models if you need them. Applies instantly, no restart. See [Model filter](#model-filter--choose-which-models-are-advertised).
- **Multi-provider routing** — every provider with a saved API key is **active at the same time**. `GET /api/tags` advertises all their models with qualified `model@Provider` names (e.g. `deepseek-v4-flash@DeepSeek`, `gemini-2.5-flash@Gemini`); each request routes to the provider that owns the requested model, and unknown names fall back to the currently selected provider. Use the qualified name when the same model exists on more than one provider.
- **API key links** — "Get API Key ↗" opens the right key page for the selected provider.
- **Automatic update check** - on startup the app asks GitHub for the newest release and shows a banner when a newer version exists, with *Download update* / *Open Microsoft Store*, *Release notes* and *Skip this version*. Can be turned off in the **Options** tab. See [Releases & update check](#releases--update-check).
- Settings persist to `%APPDATA%\AiCloudProxy\settings.json`.

## How it works

```
Your tool (Ollama client)
      │  POST http://127.0.0.1:11435/api/chat   (Ollama wire format)
      ▼
 AICloudProxy.exe  ── in-process listener ── translates ──►  Provider API
       │                                               (DeepSeek / OpenAI / Gemini / Claude / OpenRouter)
      └────────  Ollama-format stream back  ◄────────┘
```

The app translates between the Ollama wire format and each provider's native format:

| Provider | Model (default, editable) | Format translated |
|----------|---------------------------|-------------------|
| DeepSeek | `deepseek-v4-flash`        | OpenAI-compatible |
| OpenAI   | `gpt-4o-mini`             | OpenAI-compatible |
| Gemini   | `gemini-2.5-flash`        | `generateContent` |
| Claude   | `claude-sonnet-4-5-20250929` | Anthropic Messages |
| OpenRouter | `openai/gpt-oss-120b` | OpenAI-compatible |
| Custom   | (yours)                   | OpenAI-compatible |

> **Custom providers:** any OpenAI-compatible endpoint works (vLLM, LM Studio, …).
> When you add one, put the **full API path** in the Base URL field — e.g. `https://openrouter.ai/api/v1`,
> not just `https://openrouter.ai` — because the app appends `/chat/completions` and `/models` to it.
> (OpenRouter itself is now a built-in provider, so no custom setup is needed for it.)

> **Gemini note (2026):** Google now rejects old **"Standard"/unrestricted** API keys
> with `401/403`. Create a fresh key at https://aistudio.google.com/apikey — all new
> keys are "auth" keys — and paste it into the app. AI Cloud Proxy also sends the key via the
> `X-Goog-Api-Key` header (Google's recommended method).

DeepSeek also exposes `deepseek-v4-pro` and the experimental `deepseek-v4-flash-vision-exp`
(image input). Instead of typing these by hand, enter your API key and click **Get Models**
in the app — it queries the provider and fills the model dropdown automatically.

## Build & run (dev)

## 🛠️ Building from Source (For Developers)

If you wish to build or run the developer build locally:

```bash
dotnet build src/AiCloudProxy/AiCloudProxy.csproj
dotnet run --project src/AiCloudProxy/AiCloudProxy.csproj

The executable is produced at `src/AiCloudProxy/bin/Debug/net10.0-windows/AICloudProxy.exe`.

## Publishing / Packaging (MSIX & Store)

Official signed builds are packaged and published exclusively via the Microsoft Store.
To create a package for Store submission (requires Windows SDK & Partner Center configuration):

dotnet publish src/AiCloudProxy/AiCloudProxy.csproj -c Release -r win-x64 ...

## 🔄 Updates & Version Checking

Official signed updates are delivered seamlessly through the Microsoft Store.

The app includes a built-in update checker that queries GitHub (https://api.github.com/repos/local1907/AiCloudProxy/releases/latest) in the background without sending any personal data, telemetry, or API keys:

If a newer version is available, a notification banner appears above the tabs.

Open Microsoft Store — directs you straight to the Store listing to download the latest signed update.

Release notes — opens the GitHub release page to view changes.

Skip this version — hides the banner for that specific release.

Update checks fail silently when offline and can be toggled or manually triggered in the Options tab.

The [release workflow](.github/workflows/release.yml) builds the exe and attaches it to the release.

On startup the app asks GitHub for the newest release (`GET https://api.github.com/repos/local1907/AiCloudProxy/releases/latest`) and, if it is newer than the running build, shows a banner above the tabs:

- **Download update** — opens the release's `AICloudProxy.exe` (standalone builds)
- **Open Microsoft Store** — opens the Store listing (MSIX/Store builds, which update through the Store)
- **Release notes** — opens the release page
- **Skip this version** — hides the banner for that release; the next release is announced again

Nothing is sent to GitHub beyond the anonymous request itself: no ids, no settings, no API keys. The check runs in the background so startup is never delayed, fails silently when offline, and can be turned off (or run manually with **Check now**) in the **Options** tab. The current state is also shown there, e.g. *"Up to date — v1.0.15.0 is the latest version."*

## Quick start

1. Run `AICloudProxy.exe`.
2. Pick a provider (e.g. DeepSeek) and click **Get API Key ↗** to grab a key from that provider's site.
3. Paste the API key into the **API Key** box.
4. Set the **Port** (default `11435`).
5. Click **Start Proxy**.
6. In your tool that normally talks to Ollama, set the base URL to `http://127.0.0.1:11435` (or your chosen port).
7. Use the **Test** tab to send a sample question and verify everything works.

## Model filter — choose which models are advertised

`GET /api/tags` and `GET /v1/models` advertise every model the provider reports, **minus a
keyword filter**. A model whose ID contains one of the keywords is hidden, so non-chat entries
(embeddings, TTS, image/video/music generators, experimental previews, …) don't clutter your
tool's model picker.

The list is **editable in the app** — no rebuild, no restart.

1. Open the **Options** card (main window, below the API key).
2. Find **"Model filter — hidden keywords"**.
3. Edit the text — **one keyword per line**; commas and semicolons also separate, case does not
   matter, blank lines and duplicates are ignored.
4. The change applies **immediately**: click **Get Models** to refresh the dropdown, or simply
   **Start Proxy** again. The log shows
   `Model filter hides N keyword(s) — edit them in Options.`

*Example — you build games and want music/sound-effect models back:* delete the `music`,
`lyria`, `sound`, `audio`, `speech`, `tts`, `whisper` and `transcri` lines, leaving the rest
in place.

- **Clear the whole box** to hide nothing (name-based filtering off).
- **"Restore default filter list"** puts the factory list back.
- The list is stored with your other settings in `%APPDATA%\AiCloudProxy\settings.json`
  (`BannedModelKeywords`).

Factory default (29 keywords):

```
embedding, embed, dall-e, dalle, whisper, tts, speech, realtime, audio, moderation, transcri,
imagen, veo, pali, aqa, nano-banana, banana, image, music, lyria, sound, jewels, search,
tuning, gemma-2b, antigravity, computer-use, robotics, vision-only
```

> **Things to know**
> - `antigravity`, `computer-use` and `robotics` (Gemini) reject normal chat with
>   *"Multiturn chat is not enabled"* — keep them filtered unless your client handles them.
> - The proxy implements **no `/v1/embeddings` endpoint** yet, so un-hiding `embedding`/`embed`
>   only makes those models *visible*; they still cannot be used for chat through this app.
> - Provider capability metadata is applied **on top of** the keyword list: e.g. Gemini models
>   that cannot `generateContent` are always hidden, whatever the list says.

## Visual Studio 2026 Copilot (BYOM / Bring Your Own Model)

VS 2026's Copilot Chat can use AI Cloud Proxy through its **Ollama** provider. It needs the
model capabilities that AI Cloud Proxy now advertises via `POST /api/show` and `GET /api/tags`
(tool calling + token limits) — without them models get listed but never appear
in the model picker.

1. Enable **Bring Your Own Key**: `Tools → Options → search "Enable bring" →`
   tick **"Bring Your Own Key managed models endpoint"**.
2. In **Copilot Chat**, open the model picker → **Manage models / Add a model** →
   **Add model provider** → choose **Ollama**.
3. Enter the AI Cloud Proxy URL: `http://127.0.0.1:11435` (or your port).
4. Use **Agent (Preview)** mode and select one of the discovered models.

> **VS 18.10 breaking change (BYOK) — models greyed out / not selectable?** In Visual Studio 2026
> 18.10, BYOK works **only in the new **Agent (Preview)** mode ("Enhanced with the Copilot CLI
> SDK"); the older Ask/Agent BYOK experience is no longer supported. If your AI Cloud Proxy models
> load but appear greyed out, **start a NEW chat thread first** — a chat mode cannot be changed
> mid-conversation, so **Agent (Preview)** itself stays greyed in an existing thread — then pick
> **Agent (Preview)** and select your model. (VS's own tooltip: *"This mode can't be used in the
> current conversation. Start a new chat to switch to it."* and *"Your own models work only in
> Agent (Preview). Switch or create a new thread to use them."*) Also **re-add** the Ollama provider
> once, as VS 18.10 changed that provider's backend.
>
> If models still don't show up after an update, remove and **re-add** the Ollama
> provider (VS 18.10 dropped previously added Ollama models across a backend update).
> As a manual fallback you can edit
> `%USERPROFILE%\AppData\Local\Microsoft\VisualStudio\Copilot\BringYourOwnModel\ConfiguredBringYourOwnModel_v2.json`
> and set `"IsToolCallingEnabled": true` for your models.

## License

MIT License

Copyright (c) 2026 AI Cloud Proxy contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## 🛒 Download Official Signed Release

To ensure security and prevent Windows SmartScreen / Code Signing warnings, official signed builds of **AiCloudProxy** are exclusively distributed via the **Microsoft Store**.

👉 **[Get AiCloudProxy from Microsoft Store](https://apps.microsoft.com/detail/9nbb09h4p1mk?hl=en-US&gl=US)**
