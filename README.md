# whispershortkey

A Windows system-tray voice dictation tool. Press **Ctrl+Space**, speak, and transcribed text appears in any application.

## Features

- Runs silently in the Windows system tray
- Global hotkey: **Ctrl+Space** to start/stop recording
- Live audio waveform overlay during recording
- Transcribes via **OpenAI Whisper** (whisper-1, gpt-4o-transcribe, etc.) or **Google Gemini**
- Automatically pastes transcribed text into your active application
- Follows Windows light/dark theme and accent color
- API keys stored locally in your Windows user profile

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
| Toggle recording        | Double-click tray icon |
| Open settings           | Right-click tray icon → Settings |
| Exit app                | Right-click tray icon → Exit |

While recording, a small floating overlay shows your microphone input level.

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
│   └── AppSettings.cs               # Settings model
├── Services/
│   ├── AudioRecorderService.cs      # NAudio 16kHz mono recorder
│   ├── GeminiTranscriptionProvider.cs
│   ├── HotkeyService.cs             # Global Ctrl+Space handler
│   ├── ITranscriptionProvider.cs    # Pluggable provider interface
│   ├── KeyboardInjectionService.cs  # SendInput + clipboard paste
│   ├── OpenAITranscriptionProvider.cs
│   ├── SettingsService.cs           # JSON persistence in %APPDATA%
│   ├── TranscriptionService.cs      # Provider dispatch
│   └── TrayService.cs               # System tray icon & menu
├── Views/
│   ├── MicrophoneOverlayWindow.xaml # Recording widget with waveform
│   └── SettingsWindow.xaml          # Provider, key, model, language
├── App.xaml / App.xaml.cs           # Lifecycle & orchestration
└── MainWindow.xaml                  # Hidden HWND host for hotkey
```

## License

MIT
