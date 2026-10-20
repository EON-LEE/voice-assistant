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

On `/live.html` the source is chosen with right-click → **오디오 소스 설정** → **오디오 소스**: in LiveCoach.exe
**Teams 앱 소리** (the default there) or **PC 전체 소리**, in any host **탭/시스템 오디오 (Teams 등)** (share picker) or
**마이크 (대면 회의실 등)** for in-person. Then right-click → **라이브 시작**. On `/index.html` use **Live → Audio source**.

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

**Teams 앱 소리 (recommended for the Teams desktop app).** LiveCoach captures only the Teams desktop app's audio itself,
on whichever device Teams plays to (headset included), with no share picker. It needs **Windows 11 or Windows 10 build
20348+**; otherwise the page says 'Teams 앱 소리' is unavailable and ends the session. The status line names the source.

- "Teams가 실행 중이 아닙니다": start Teams; capture attaches by itself (checked every 2 s).
- "Teams에서 소리가 감지되지만 캡처가 비어 있습니다" (`teams-silent`): Teams plays sound but the capture is silent, a
  known limitation of the new Teams app. Press **PC 전체 소리로 전환**; the meeting session continues.
- "Teams 앱 소리가 들어오지 않습니다 (15초)" while you hear the meeting: switch to **PC 전체 소리**.
- Check before a meeting with Teams **Settings → Devices → Make a test call**: the input meter must move.

**PC 전체 소리.** Loopback of the Windows **default output device**: every app on it, nothing on other devices (see
the silent-share section below).

**탭/시스템 오디오 (share picker).** The WebView2 picker offers screens and windows, **no browser tabs**. Choose
**Entire screen** with **Share system audio**; a window share has no audio. If the app says it cannot start
screen-audio sharing, update the Microsoft Edge WebView2 Runtime or open `/live.html` in Edge or Chrome instead.

In a browser, Teams 앱 소리 and PC 전체 소리 are not offered ("LiveCoach 앱에서만 쓸 수 있습니다"); use a Teams web tab or the
share picker.

## Microphone mode (in-person)

Choose **마이크 (대면 회의실 등)** as the audio source and the device under **마이크**, inform the participants, then
**라이브 시작**. Check:

- Browser: allow the microphone in the site permissions (address bar), then 다시 시작.
- LiveCoach.exe: Windows **Settings → Privacy & security → Microphone** must allow desktop apps.
- "No microphone was found" or "busy": connect the device, close other apps that hold it, pick the device again.
- "muted by the system/browser": unmute the device or the OS mute key; capture resumes by itself.

## Silent share: muted or wrong default output device

"소리가 들어오지 않습니다 (15초)" with system audio almost always means Teams plays to a device that is not the Windows
default output, so the loopback is silent. In LiveCoach.exe, **Teams 앱 소리** avoids this because it follows Teams'
own device. Otherwise:

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
