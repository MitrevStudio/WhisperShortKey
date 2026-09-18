# whispershortkey

A Windows system-tray voice dictation tool. Press **Ctrl+Space**, speak, and transcribed text appears in any application.

## Features

- Runs silently in the Windows system tray
- Global hotkey: **Ctrl+Space** to start/stop recording, **Esc** to cancel
- Live audio waveform overlay during recording
- Transcribes via **OpenAI Whisper** (whisper-1, gpt-4o-transcribe, etc.) or **Google Gemini**
- Automatically pastes transcribed text into your active application
- **A failed recording is never lost** — the audio is kept and can be retried from the tray
- **History** of every transcript and recording, searchable, with playback
- Follows Windows light/dark theme and accent color
- API keys stored locally in your Windows user profile

## While it works

The floating panel stays up for the whole trip, so there is never a moment where nothing
seems to be happening:

- **Listening** — bars follow your microphone level.
- **Transcribing…** — bars sweep while the provider is working.
- **On failure** — the panel turns into the error itself, with what went wrong and a
  **Retry** button. It stays until you retry or close it; the recording is safe either way.

## History

Right-click the tray icon → **History…**

Every recording is listed newest first with its transcript, how long it was, which model
produced it, and whether it succeeded. From there you can:

- **Play** the original audio, to check a transcript against what was actually said
- **Copy text** to the clipboard
- **Re-transcribe** any recording under your *current* settings — the way to redo something
  with a different model or language
- **Delete** one recording, or **Clear history** entirely
- **Search** across transcripts, providers and models

## Retrying a failed recording

If a transcription fails — no connection, a rate limit, a wrong API key — the recording is
kept instead of being thrown away, and the tray icon gets an amber badge.

- Right-click the tray icon: a failed recording appears at the top with the time, its length
  and why it failed. Click it to try again. With more than one, they collect in a
  **Failed recordings** submenu with **Retry all** and **Discard all**.
- Or just click the error balloon when it appears — that retries the recording it was about.

Nothing is retried automatically, so a failure never turns into a surprise burst of API
calls. A successful retry puts the text on the **clipboard** and tells you to press Ctrl+V,
rather than typing it into whatever window happens to be in front by then.

## Where things are kept

Everything lives in `%APPDATA%\VoiceTray\records` — one JSON per recording next to its WAV.

Transcripts outlive their audio, because text is kilobytes and audio is megabytes:

| | Kept for |
|---|---|
| Transcript | 90 days, or 500 recordings |
| Audio | 14 days, or 1 GB total |
| A **failed** recording | Until you retry or delete it — never expires |

Use **Clear history** in the History window if you would rather nothing was kept.

## Requirements

- Windows 10 or later
- .NET Desktop Runtime 10.0
- A microphone
- API key for OpenAI or Google Gemini

## Install

Download the latest `VoiceTray.exe` from [Releases](https://github.com/your-org/whispershortkey/releases) and run it.

Or build from source:

```powershell
dotnet restore
dotnet build -c Release
dotnet publish -c Release -o publish
```

The output is in `publish/VoiceTray.exe`.

## Setup

1. Launch `VoiceTray.exe` — a tray icon appears near the clock.
2. Right-click the tray icon → **Settings**.
3. Choose your provider (OpenAI or Gemini).
4. Enter your API key.
5. Select a model.
6. Optionally set the language for better transcription.
7. Click **Save**.

## Usage

| Action                  | How                    |
|-------------------------|------------------------|
| Start / stop recording  | Ctrl+Space             |
| Cancel a recording      | Esc (while recording)  |
| Toggle recording        | Double-click tray icon |
| Retry a failed recording| Retry in the panel, the tray menu, or click the error balloon |
| Browse past transcripts | Right-click tray icon → History… |
| Open settings           | Right-click tray icon → Settings |
| Exit app                | Right-click tray icon → Exit |

While recording, a small floating overlay shows your microphone input level.

## How long can one recording be?

Recording stops by itself, so a forgotten session cannot run away with your disk or quota.
Two ceilings apply and **the lower one wins**:

| | Limit |
|---|---|
| Your setting | **Max recording** in Settings — 1 to 60 minutes, 10 by default |
| OpenAI | 25 MB per file → about **13 min** |
| Gemini | 20 MB per request, and audio grows by a third once encoded → about **7 min** |

At 16 kHz mono a recording is 1.92 MB per minute, which is where those numbers come from.
The Settings window spells out the ceiling for whichever provider is selected.

Near the end of a recording the panel replaces the `Ctrl+Space` hint with a countdown —
amber, then red for the final ten seconds — so you can finish your sentence rather than be
cut off. If the cap is reached anyway, whatever was captured is still transcribed.

Gemini's own limit is 9.5 hours of audio; the ~7 minutes here comes from sending the audio
inline in the request. Switching to OpenAI is the simple way to record for longer.

## Models

### OpenAI
- whisper-1
- gpt-4o-mini-transcribe
- gpt-4o-transcribe
- gpt-4o-transcribe-diarize

### Gemini
- gemini-3.5-flash
- gemini-3.1-pro-preview
- gemini-3-flash-preview
- gemini-3.1-flash-lite
- gemini-2.5-flash
- gemini-2.5-flash-lite
- gemini-2.5-pro

## Project Structure

```
whispershortkey/
├── Models/
│   ├── AppSettings.cs               # Settings model
│   └── TranscriptionJob.cs          # One recording on its way to text
├── Services/
│   ├── AtomicFile.cs                # Write-then-rename
│   ├── AudioPlaybackService.cs      # Plays a stored recording back
│   ├── AudioRecorderService.cs      # NAudio 16kHz mono recorder
│   ├── GeminiTranscriptionProvider.cs
│   ├── HotkeyService.cs             # Global Ctrl+Space and Esc handler
│   ├── ITranscriptionProvider.cs    # Pluggable provider interface
│   ├── JobStore.cs                  # Durable records\ directory + retention
│   ├── KeyboardInjectionService.cs  # SendInput, clipboard, focus
│   ├── OpenAITranscriptionProvider.cs
│   ├── SettingsService.cs           # JSON persistence in %APPDATA%
│   ├── TextDeliveryService.cs       # Where the text goes, on an STA thread
│   ├── TranscriptionException.cs    # Typed failures + classifier
│   ├── TranscriptionQueue.cs        # Job lifecycle, retry, history
│   ├── TranscriptionService.cs      # Provider dispatch
│   └── TrayService.cs               # System tray icon & menu
├── Views/
│   ├── HistoryWindow.xaml           # Transcripts, playback, re-transcribe
│   ├── MicrophoneOverlayWindow.xaml # Listening / transcribing / error panel
│   └── SettingsWindow.xaml          # Provider, key, model, language
├── App.xaml / App.xaml.cs           # Lifecycle & orchestration
└── MainWindow.xaml                  # Hidden HWND host for hotkey
```

## License

MIT
