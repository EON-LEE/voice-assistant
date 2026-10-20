# Capture troubleshooting: "No shared audio"

Live Coach hears the meeting only through an audio track the browser or LiveCoach.exe hands to it. Video is never sent.
The message names the problem; the fix depends on where the page runs.

| On-screen message (start) | Meaning |
| --- | --- |
| 공유한 화면에 소리가 없습니다 / *No shared audio was provided* | The chosen share has no audio track |
| 화면 공유가 시작되지 않았습니다 | The picker was cancelled or closed |
| 화면 공유가 중지되었습니다 | Sharing stopped (Stop sharing, or the tab or window closed) |
| 공유한 소리가 잠시 끊겼습니다 | The source muted itself; capture resumes automatically |
| 소리가 들어오지 않습니다 (15초) | A track exists but has carried silence for 15 seconds |
| 화면 공유가 시스템 또는 조직 정책으로 차단되었습니다 | OS or organization policy blocks screen capture |

After any fix press **다시 공유** (re-share) or **라이브 시작**. The input meter (입력 음량) in the status row must move
when someone speaks; turn on **자막** (captions) to see what is recognized.

On `/live.html` (browser and LiveCoach.exe) the source is chosen with right-click → settings → **오디오 소스**:
**탭/시스템 오디오 (Teams 등)** for meetings, **마이크 (대면 회의실 등)** for in-person. Then right-click →
**라이브 시작**. On `/index.html` use **Live → Audio source**.

## Browser (Edge or Chrome)

**Teams on the web (recommended).** Join the meeting in an Edge or Chrome tab. Start Live Coach with 탭/시스템 오디오,
choose the **tab** that runs Teams in the picker and switch on **Share tab audio** (탭 오디오도 공유). Only that tab's
sound is captured.

**Teams desktop app.** Choose **Entire screen** (전체 화면) and tick **Share system audio** (시스템 오디오 공유). System
audio is the loopback of the Windows **default output device** and includes every app's sound; mute other apps.

**A window share has no audio.** Choosing a single window (창) never carries sound; share again with one of the
options above.

The page must be opened over HTTPS (or localhost) in a current Edge or Chrome. "이 브라우저는 화면 오디오 공유를 지원하지
않습니다" means the browser lacks display-audio capture; update it or switch browser.

## Inside LiveCoach.exe

The WebView2 share picker offers screens and windows, **no browser tabs**. Choose **Entire screen** with
**Share system audio**; a window share has no audio. If the app says it cannot start screen-audio sharing, update the
Microsoft Edge WebView2 Runtime or open `/live.html` in Edge or Chrome instead.

## Coming: native Teams capture

A native Teams audio capture path for LiveCoach.exe is planned but **not released**. Until it ships, use Entire screen
with system audio in LiveCoach.exe, or a Teams web tab in a browser. This section will describe its checks when it
ships.

## Microphone mode (in-person)

Choose **마이크 (대면 회의실 등)** as the audio source and the device under **마이크**, inform the participants, then
**라이브 시작**. Check:

- Browser: allow the microphone in the site permissions (address bar), then 다시 시작.
- LiveCoach.exe: Windows **Settings → Privacy & security → Microphone** must allow desktop apps.
- "No microphone was found" or "busy": connect the device, close other apps that hold it, pick the device again.
- "muted by the system/browser": unmute the device or the OS mute key; capture resumes by itself.

## Silent share: muted or wrong default output device

"소리가 들어오지 않습니다 (15초)" with system audio almost always means Teams plays to a device that is not the Windows
default output, so the loopback is silent.

1. Teams **Settings → Devices → Speaker**: choose the device you listen on.
2. Windows **Settings → System → Sound → Output**: make the same device the default (or set the Teams speaker to the
   default device).
3. Make sure the meeting is not muted in Teams, the tab is not muted (tab speaker icon), and the volume is not 0.
4. Bluetooth headsets switch to a low-quality hands-free profile during calls; that still works (the page resamples
   8 kHz input), but the headset must be the default output.
5. Share again (**다시 공유**) after changing devices.

## Still failing

- Try the same share in another browser profile without extensions.
- Check whether company policy blocks screen capture (for example, Edge `ScreenCaptureAllowed`); ask IT.
- Use microphone mode as a fallback for in-person meetings only; there is no automatic microphone fallback for remote
  meetings.
