# DeskCast

Windows capture app for a [verpixeld-panel](https://github.com/Jan1503/verpixeld-panel) wall. Capture a **movable region** of the desktop or a **window**, downscale to 256×128, and stream it.

The old working name was DesktopStreamer. The GitHub repository name can be changed later (Settings → General → Rename); old URLs redirect.

Two protocols:

```
[ Windows PC ]                                    [ Panel ]
DeskCast  -- UDP/PixPlane :7777 ---------------->  RP2350 + W6300  (direct, default)

[ Windows PC ]                                    [ Raspberry Pi ]              [ Panel ]
DeskCast  -- UDP/TPM2.NET :65506 --------------->  verpixeld                    HDMI / LED
                                                   (Network Stream Player)
```

## Setup

DeskCast expects [PixPlane](https://github.com/Jan1503/pixplane) as a **sibling** folder (this is how the RGB-Display tree is laid out):

```bash
git clone https://github.com/Jan1503/pixplane.git
git clone https://github.com/Jan1503/deskcast.git
```

```
pixplane/          C# panel protocol library
deskcast/          this app (local folder may still be named DesktopStreamer)
```

TPM2.NET lives inside this repo (`third_party/Tpm2Net`); no extra clone.

## Run

```powershell
dotnet run -c Release -- --ip 192.168.10.181 --port 7777 --fps 30
```

A borderless frame appears:

- **Drag** it by the dark header bar; **resize** by dragging any edge/corner.
- The hollow (magenta-free) center is transparent and click-through — whatever is behind it is what gets streamed.
- **Gear**: settings (protocol, IP, ports, fps, colour, panel mode).
- **Seam**: live 9-point tone curve + optional gain/lift (PixPlane path only). See [Scan-home seam correction](#scan-home-seam-correction).
- **X**: quit (window position + settings are remembered in `desktopstreamer.json` next to the exe).

### Direct panel (PixPlane, default)

1. Panel firmware [verpixeld-panel](https://github.com/Jan1503/verpixeld-panel) on the LAN, UDP **7777**.
2. Colour bits must match the firmware (8 or 14). The settings dialog can push `mode` + reboot via HTTP `:5000`. Live 8/14 switching without reboot (`livemode`) is a verpixeld-host feature (firmware 1.7+); DeskCast keeps a fixed `--bits` for the session.

### Legacy TPM2 (verpixeld)

1. Add a **Network Stream Player** extension to a canvas (its size = the resolution you stream).
2. Note the **Actual Port** (default `65506`) and the Pi's IP.
3. Set protocol to TPM2. Auto-config queries the player for canvas size; if nobody answers, it falls back to 256×128.

## Scan-home seam correction

The ICND1065L 64-scan layout maps the first line of each scan group to logical columns **63 / 127 / 191 / 255**. That line is nonlinear: too dark in the shadows and too bright in the highlights. It is a **chip/panel artifact**, not four module joints, and the firmware cannot make it physically linear (timing / OE / register experiments add ghosts instead).

DeskCast’s **Seam** window is a host-side workaround in [PixPlane](https://github.com/Jan1503/pixplane):

1. Nine knots (in 0, 32, …, 255 → out 0..255), shared by all four columns, remapped **before** gamma.
2. Optional per-column Gain / Lift still apply **after** the curve (set them to 1.0 / 0 while matching).
3. Changes go to the running sender immediately. TPM2 mode ignores the remap (the receiver has no per-column packer).

To match: show a grey window behind the overlay, set Gain/Lift → 1 / 0, then nudge the knot for that grey until the seam column matches its neighbour. Repeat at a few levels. Firmware `t f` bypasses DeskCast, so it cannot calibrate this curve.

<!-- PHOTO seam-uncorrected: DeskCast grey capture, scan-home columns visible -->
<!-- PHOTO seam-corrected: same after the 9-point curve -->

Knots persist in `desktopstreamer.json` with the rest of the sender settings.

## CLI

| Flag          | Meaning                                      | Default            |
|---------------|----------------------------------------------|--------------------|
| `--protocol`  | `rgbpanel` (PixPlane) or `tpm2`              | `rgbpanel`         |
| `--ip`        | Receiver IP                                  | `192.168.10.181`   |
| `--port`      | PixPlane UDP port                            | `7777`             |
| `--tpm2port`  | TPM2 UDP port                                | `65506`            |
| `--fps`       | Target frames per second                     | `30`               |
| `--mbps`      | PixPlane send pacing                         | `19`               |
| `--bits`      | Greyscale bit-planes (8/10/13/14)            | `14`               |
| `--gamma`     | Gamma (1.0 = off)                            | `1.0`              |

Windows-only (`net10.0-windows`, GDI `CopyFromScreen`). Runs DPI-unaware so capture coordinates match the overlay on scaled displays. Intended for a local network.

## Layout

```
DesktopStreamer.csproj
OverlayForm.cs / SettingsForm.cs / SeamForm.cs
PanelSender.cs              PixPlane + TPM2 adapters
third_party/Tpm2Net/        Vendored TPM2.NET sender
```

## License

MIT. See `LICENSE`. PixPlane is a separate MIT repo cloned next to this one.
