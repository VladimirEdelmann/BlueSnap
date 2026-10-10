# 🔊 BlueSnap

**Press the Bluetooth button on your speaker, and your Windows PC connects to it automatically.**

Many Bluetooth speakers switch between your phone and your PC at the press of a button. Windows never takes
the speaker back on its own: you have to open Settings → Bluetooth and click **Connect** every time.
BlueSnap does that click for you as soon as the speaker is free. It runs silently in the
notification area.

* ⚡ **Fast.** It connects within a few seconds of the speaker becoming available.
* ✅ **Real connections.** It checks that the stereo (A2DP) audio link is actually up, not just that the
  device is listed.
* 🔁 **Hands-free.** It starts at sign-in, reconnects after power-off, adapter resets and reboots, and backs
  off politely while the speaker is away.
* 🎧 **Plays nicely.** It never re-pairs and never resets the adapter. It searches more gently while other
  Bluetooth headphones are playing.
* 🪶 **Tiny, with no dependencies.** It is one ~40 KB exe, built on *your* PC by the C# compiler that ships
  with Windows. It needs no admin rights, no installers and no downloads.
* 🔊 **Optional default output.** It can make the speaker the default audio output when it connects.

Supported systems are Windows 10 and Windows 11, for any Bluetooth speaker or headphones that are already
paired. It has been tested with a JBL GO Essential.

---

## Install (2 minutes)

1. **Pair your speaker once** in Settings → Bluetooth & other devices, if you haven't already.
2. **Get the files.** On GitHub, use **Code → Download ZIP** and unzip it, or run:
   ```bash
   git clone https://github.com/VladimirEdelmann/BlueSnap.git
   ```
3. **Find your speaker's address.** Open PowerShell in the folder and run:
   ```bash
   powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
   ```
   The installer lists your paired Bluetooth audio devices:
   ```
   Address            State                        Link     Endpoint name
   12:34:56:78:9A:BC  UNPLUGGED (not connected)    down     Speakers (JBL GO Essential Stereo)
   ```
4. **Install for that address:**
   ```bash
   powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -Address 12:34:56:78:9A:BC
   ```

That's it. A small Bluetooth icon appears in the notification area. It is **blue** when the speaker is
connected and **grey** when it is not. The tool also starts automatically every time you sign in.

### Uninstall
```bash
powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1
```
This removes the program and its autostart entry. Your pairing and sound settings are left untouched.

## Tray menu
| Item | What it does |
|---|---|
| **Connect now** | Tries to connect immediately. Double-clicking the icon does the same. |
| **Hand off to another device** | Disconnects the speaker and leaves it alone for 2 minutes, so your phone can take it. |
| **Pause auto-connect** | Stops auto-connect until you resume it or restart the tool. |
| **Open log** | Shows what the tool has been doing. |
| **Exit** | Stops the tool until the next sign-in. |

## Settings
Settings live in `%LOCALAPPDATA%\BlueSnap\config.ini`. Restart the tool after editing it. The
main options are:

| Setting | Default | Meaning |
|---|---|---|
| `SetAsDefault` | `true` | Make the speaker the default output when it connects |
| `HandOffMinutes` | `2` | How long **Hand off** leaves the speaker alone |
| `HoldOffAfterDisconnectSec` | `0` | Wait this long after the speaker leaves the PC. Set it to 30 if you hand the speaker to your phone with its *button*. |
| `FastGapSec` / `SlowGapSec` | `2` / `20` | Pause between attempts: during the first hour / later |
| `SlowWhileOtherBtAudioActive` | `true` | Search less often while other Bluetooth headphones are playing |

Every option is explained in [`config.example.ini`](config.example.ini).

## FAQ

**Why does it "try" every few seconds instead of reacting instantly?**
Windows gets no signal when a paired speaker becomes free. Classic Bluetooth simply doesn't announce it.
The only way to find out is to knock, meaning a connection attempt. While the speaker is away, the tool
knocks every few seconds. That is cheap and invisible. Once the speaker is connected, the tool sends no
Bluetooth traffic at all.

**Will it steal the speaker from my phone?**
Most speakers accept only one source at a time. They ignore the PC while the phone is playing, until you
press the button. Some "multipoint" speakers accept two sources at once; with those, the PC may join
alongside the phone.

**Windows SmartScreen or antivirus complains?**
The installer compiles the exe on your PC from the source in `src/`, so nothing pre-built is downloaded.
You can read every line of the source before running it.

**Something isn't working?**
Run `bluesnap-cli status` in `%LOCALAPPDATA%\BlueSnap`. It shows the adapter, pairing, link and
audio endpoint state, and says clearly whether audio is connected. Include its output and `agent.log` when
you open an issue.

## How it works
The tool sends the same documented driver request that the **Connect** button in Settings uses,
`KSPROPERTY_ONESHOT_RECONNECT`. It then waits for the speaker's audio endpoint to become active. See
[docs/how-it-works.md](docs/how-it-works.md) for the APIs, the limitations, the diagnostics and a full test
procedure.

## Building manually
```bash
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```
This produces `bin\BlueSnap.exe` (the background agent, which has no window) and
`bin\bluesnap-cli.exe` (the console tool for diagnostics). Both are built from
[`src/BlueSnap.cs`](src/BlueSnap.cs).

## License
[MIT](LICENSE)
