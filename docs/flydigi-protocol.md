# Flydigi VADER 5 Pro battery protocol

How BatteryBar reads the battery of a Flydigi VADER 5 Pro connected through its 2.4G receiver. This is a
description of the device's wire format for interoperability; it contains no code or data from Flydigi's
software. The same request and battery byte are implemented independently in SDL3's open-source Flydigi
driver ([`src/joystick/hidapi/SDL_hidapi_flydigi.c`](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_flydigi.c),
zlib license), which is a good second reference.

Code: `src/BatteryBar.Core/Flydigi/FlydigiProtocol.cs` (pure decoder, unit tested) and
`src/BatteryBar.Core/Providers/FlydigiProvider.cs` (HID I/O).

## Why not XInput

XInput reports the pad as `BATTERY_TYPE_WIRED`, because the receiver is a wired USB device. The real level
is only available from the receiver's vendor HID interface.

## Receiver interfaces (VID `0x37D7`, PID `0x2401`)

| Interface | Kind | Usage page / usage | Report sizes in / out / feature | Report id | Note |
|---|---|---|---|---|---|
| MI_00 | XInput (XUSB) | - | - | - | battery always "wired" |
| **MI_01** | HID vendor | **`0xFFA0` / `0x0001`** | **33 / 33 / 0** | **none** | **command channel used by BatteryBar** |
| MI_02 | HID mouse | `0x0001` / `0x0002` | 7 / 0 / 0 | 2 | virtual mouse |
| MI_03 | HID vendor | `0xFFEE` / `0x0000` | 64 / 64 / 64 | 5 | not used |
| IG_01 | HID gamepad | `0x0001` / `0x0005` | 15 / 0 / 0 | 0 | HID view of the XInput pad |

The command interface is chosen by: VID `0x37D7`, usage page `0xFFA0`, PID high nibble `2` and
`PID & 0xFF00 != 0x2800`. Device instance paths differ per machine, so the interface is never looked up by path.

## Request: heartbeat (status query)

A 33-byte output report written with `WriteFile`. It only asks the receiver for its status; it changes no
setting.

```
00 5A A5 01 02 03 00 00 ... 00   (33 bytes in total)
|  |  |  |  |  '- checksum: (0x01 + 0x02) & 0xFF
|  |  |  |  '---- payload length (2)
|  |  |  '------- command 0x01 = heartbeat / device info
|  '--'---------- magic bytes 0x5A 0xA5
'---------------- report id (the interface has none, so 0)
```

SDL sends the same command with checksum `0x00`; the receiver answers either way. BatteryBar sends `0x03`.

## Answer

A 33-byte input report, about 25 ms after the request. Below, `data` is the report without the leading report
id byte.

| Offset | Example | Meaning |
|---|---|---|
| 0-1 | `5A A5` | magic bytes |
| 2 | `01` | command id; must be `0x01` for a heartbeat answer |
| 3 | `01` | packet count |
| 4 | `00` | packet index; only packet 0 carries the status block |
| 5 | `82` | device type: 130 = VADER 5 Pro, 0 = no controller connected |
| 6 | `02` | connection: 1 wired, 2 receiver, 3 Bluetooth |
| 7-10 | `00 00 00 00` | MAC address (reversed) |
| **11** | **`05`** | **battery byte** |
| 12- | | chip type and firmware versions (not used) |
| 31 | | checksum |

Example answer (MAC zeroed):
`5AA50101008202000000000545010071540467351700000000000010261F0037`

### Battery byte

- Low nibble (`b & 0x0F`): level 0-5. 0 means unknown.
- High nibble (`b >> 4`): 0 on battery, 1 charging, 2 full (charge done).
- The level is coarse, not a measured percentage. BatteryBar shows `level x 20` % (as SDL does), full = 100 %,
  and writes "level n/5" in the tooltip. Level 1 (20 %) counts as low battery.
- Other Flydigi models may encode the byte differently. For any device type other than 130 the value is shown
  as unknown rather than guessed.

## Timing

- The receiver sends nothing on this interface while the pad is idle; the battery has to be asked for.
- A request that follows the previous one within about one second is ignored. BatteryBar waits at least
  1.5 s between requests, uses a 600 ms timeout and at most three attempts, and polls once a minute.
- The interface is opened with `FILE_SHARE_READ | FILE_SHARE_WRITE`, so Flydigi's own software can keep using
  it at the same time. Answers with another command id (traffic of other software) are ignored.
- No administrator rights are needed.

## Not verified

- Behaviour while the pad is off or asleep: BatteryBar treats "no answer", device type 0 and level 0 as
  "not connected" or "unknown".
- The charging and full states were not seen on real hardware; the decoder follows the encoding above.
- Bluetooth mode was not tested; only the 2.4G receiver.
- Only the 2.4G receiver's vendor interface is used; without the receiver the battery cannot be read.
