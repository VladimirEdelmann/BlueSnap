# How it works

This page is the technical background for BlueSnap. It explains:
* which Windows mechanisms the tool uses and why;
* what Windows cannot do;
* how to test the tool on your own hardware.

## The short version

```
 speaker becomes free          PC pages the speaker          driver opens A2DP           endpoint turns ACTIVE
 (button press / power on) ──► KSPROPERTY_ONESHOT_RECONNECT ──► (same as Settings ──►  ✔ verified, optional
                                every few seconds              "Connect" button)        set as default output
```

## Questions investigated

### Can Windows detect when a paired speaker becomes connectable?
**Not as an event.** A classic Bluetooth (BR/EDR) speaker that becomes connectable does not announce itself
to the devices it is already paired with. The PC can find out in one of two ways:

* **Page** the speaker, which means trying to connect to its address. This is cheap and targets only that
  speaker.
* Run an **inquiry scan**. This only sees the speaker while it is discoverable. It takes about 10 s and
  disturbs the radio more.

Windows offers no "paired device X is now available" event for classic Bluetooth. The tool therefore pages
the speaker while it is disconnected. For this tool, a page *is* a connect attempt, so "detect" and
"connect" are the same step.

Some changes *are* reported as events, and the tool uses them:

* connect
* disconnect
* re-pairing

These arrive through `IMMNotificationClient`.

### Can an A2DP connection be started programmatically?
**Yes.** [`KSPROPERTY_ONESHOT_RECONNECT`](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-oneshot-reconnect)
is a documented property that prompts the Bluetooth audio driver to try connecting. It is a GET request with
no value buffer, sent to the KS filter of the A2DP driver. It is what the "Connect" button in Settings uses.

The call returns as soon as the driver *starts* the attempt, so the tool checks the result separately.
[`KSPROPERTY_ONESHOT_DISCONNECT`](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/kspropsetid-btaudio)
is the opposite request. The tool uses it for "Hand off to another device".

### Which APIs are used
| Purpose | API |
|---|---|
| Find the speaker's audio endpoint | `IMMDeviceEnumerator::EnumAudioEndpoints`, then `IDeviceTopology::GetConnector(0)` and `IConnector::GetDeviceIdConnectedTo`. The resulting KS filter path contains `bthenum#{0000110b-…}` (A2DP AudioSink) and the Bluetooth address. |
| Connect / disconnect | `IMMDeviceEnumerator::GetDevice(filterId)`, then `Activate(IKsControl)` and `KsProperty(KSPROPSETID_BtAudio, ONESHOT_RECONNECT / ONESHOT_DISCONNECT, GET)`. If that fails, the tool falls back to `CreateFile` on the filter plus `DeviceIoControl(IOCTL_KS_PROPERTY)`. |
| Check that audio really is connected | Endpoint state `DEVICE_STATE_ACTIVE` (`IMMDevice::GetState`) |
| Connect / disconnect events | `IMMNotificationClient::OnDeviceStateChanged` |
| Link and pairing state (diagnostics) | `BluetoothGetDeviceInfo` |
| Adapter present | `BluetoothFindFirstRadio` |
| Set default output (optional) | `IPolicyConfig::SetDefaultEndpoint`. This is **undocumented** but stable since Windows 7, and SoundSwitch, EarTrumpet and AudioDeviceCmdlets use it. Turn it off with `SetAsDefault=false`. |

The constants and COM vtable layouts come from the Windows SDK 10.0.19041 headers: `ksmedia.h`, `ks.h`,
`mmdeviceapi.h`, `devicetopology.h` and `bluetoothapis.h`.

### "Status = OK" is not "connected"
Here is a disconnected speaker, as Windows reports it:

* `Get-PnpDevice` shows `Status=OK` and `DEVPKEY_Device_IsPresent=True`.
* The real state is different: IsConnected (`{83DA6326-…} 15`) is `False`, and the A2DP endpoint is
  `UNPLUGGED (0x8)`.

The tool only treats the speaker as connected when the A2DP endpoint is **ACTIVE**.

### Why C# and not pure PowerShell?
PowerShell has no built-in cmdlet that connects a Bluetooth audio device. Doing it requires COM vtable
interop (`IKsControl`, `IDeviceTopology`) and an event sink that runs on COM threads. From PowerShell that
means `Add-Type` C# compiled at every start, inside a long-running `powershell.exe`.

Instead, the tool is one C# file, compiled once by the **compiler that ships with Windows** (.NET Framework
4.x `csc.exe`).

* There are no packages and no SDK.
* It runs as a ~40 KB exe.
* It uses about 15–20 MB of memory and practically no CPU.

### What if the speaker is connected to another device?
* **Single-link speakers** don't accept a second source while they are streaming. The PC's page fails and
  the tool simply retries until the speaker is free. Most cheap speakers are like this.
* **Multipoint speakers** may accept the PC *as a second source* while still on the phone. That is a
  feature of the speaker, not something Windows controls.
* Windows cannot force a speaker to drop another device.
* If you want the phone to have the speaker, use **Hand off to another device** in the tray. It disconnects
  the speaker and suspends auto-connect for `HandOffMinutes`.

### What does the speaker's Bluetooth button do?
This depends on the model and can't be detected from Windows. A typical JBL disconnects the current source
and enters **pairing mode**, which is discoverable *and* connectable. Either way, the speaker now answers
pages from devices it knows, and that is all the tool needs. It never re-pairs.

### The limitation
There is no "became available" event, so the reaction time depends on the retry pace:

| Situation | Reaction time |
|---|---|
| First hour after the speaker became unavailable | **≤ ~8 s** |
| Later | **≤ ~26 s** |
| Another Bluetooth audio device is playing | **≤ ~26 s**. The tool pages less often so that device doesn't drop out. |

All of these values are configurable. When the speaker reconnects *by itself*, as many do after power-on,
the connection is instant.

## Behaviour
| Situation | What happens |
|---|---|
| Connected (A2DP endpoint ACTIVE) | Idle. It waits for the disconnect event and makes no Bluetooth traffic. |
| Speaker unavailable | One attempt (up to 6 s), then a 2 s gap. After 60 min, or while another BT audio device is active, the gap is 20 s. Only one attempt runs at a time, guarded by a named mutex. |
| Attempt succeeds | It logs "Connected", resets the back-off and, if needed, makes the speaker the default output. |
| Hand-off (tray) | It disconnects the speaker and suspends auto-connect for 2 min. "Connect now" ends the hand-off early. |
| Adapter missing or disabled | It logs this once and checks again every 10 s. |
| Speaker unpaired, or endpoint disabled in Sound settings | It logs this once and checks again every 60 s. It never pairs. |
| Unexpected COM error | It re-initialises after 10 s instead of crashing. |

The log is at `%LOCALAPPDATA%\BlueSnap\agent.log`. It records state changes and occasional
summaries, and is capped at 2 × 512 KB.

## Diagnostic commands
Run these from `%LOCALAPPDATA%\BlueSnap`.

| Command | What it does |
|---|---|
| `bluesnap-cli list` | Lists paired A2DP devices with their address, endpoint state and link state |
| `bluesnap-cli status` | Shows full diagnostics. Exit code 0 means connected. |
| `bluesnap-cli connect --timeout 15 --verbose` | Makes one real attempt. Exit code 0 means the endpoint became ACTIVE. |
| `bluesnap-cli disconnect` | Drops the audio connection. A running agent reconnects it. |
| `bluesnap-cli set-default` | Makes the speaker the default output |
| `bluesnap-cli stop` | Stops the agent gracefully |
| `bluesnap-cli` | Runs the agent in the console (stop it with Ctrl+C) |

## Test procedure
Before you start, open the log (tray icon, then **Open log**) and run `bluesnap-cli status` after each step.

1. **Speaker already connected.** Start the agent. *Expected:* `Connected … A2DP endpoint is ACTIVE`, and no
   further attempts.
2. **Speaker on the phone, then press its Bluetooth button.** *Expected:* `Connected … after N attempt(s)`
   within about 8 s.
3. **Speaker connected to the PC, press its Bluetooth button.** *Expected:* `Disconnected`, then
   `Connected` again within a few seconds.
4. **Hand-off.** In the tray, choose **Hand off to another device**, then connect from your phone.
   *Expected:* the phone keeps the speaker. Two minutes later the PC tries again, without disturbing the
   phone if the speaker is single-link.
5. **Speaker off, then on.** *Expected:* one `Speaker not reachable…` line, then `Connected` after power-on.
6. **Bluetooth adapter disabled** (Device Manager or unplugging the dongle). *Expected:* one warning, no
   spam, and a reconnect after you re-enable it.
7. **PC restarted.** *Expected:* `Agent started` shortly after sign-in, then `Connected`. Only one process
   should be running.
8. **Speaker unavailable for a long time.** *Expected:* one `slowing retries` line after 60 min, then one
   `Still waiting…` line every 30 minutes.
