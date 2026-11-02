**English** · [Русский](README.ru.md)

# Cube Radar: Hidden Cube Radar for Aion 2

**Cube Radar** is a mini-radar overlay for **Aion 2 (Global)** that shows live where **Hidden Cubes** are: distance,
direction and height. It reads server→client traffic with Npcap, the same way Wireshark does. It does no memory
reading and no injection, and it sends nothing to the game. The only internet connection is the optional update check
on GitHub (the finished exe only).

The interface is Russian by default. English is a switch in the settings and in the web view. The
[guide](docs/ANLEITUNG.md) stays German.

- **Mini-radar over the game:** nearby Hidden Cubes with distance, direction and height (▲ above you, ▼ below you),
  and a sound for every new cube
- **A status for each cube:** visible, out of view, being opened, opened, gone
- **Enemies on the same radar:** red diamonds and a list with name, faction, PvP rank and distance (Global/Abyss,
  first test version). Five rank codes are checked against screenshots; the rest of the assumed order is marked with `*`
- **Web view** with a map, a list and its own spot database (export as CSV/JSON)
- **One exe** for Windows 10/11, no installation, updates itself on click
- Confirmed in **Verteron**; in other areas the radar learns the cube id on its own
  ([Where does it work?](docs/ANLEITUNG.md#7-wo-funktioniert-es))

## Download

The Windows 10/11 (64-bit) build is on [**Releases**](https://github.com/AsseylumVA/aion2-cube-radar/releases/latest):
download `Cube.Radar.exe` and double-click it. Nothing else has to be installed; .NET is included. You do need
[Npcap](https://npcap.com) (the installer with its default options). On startup Windows asks for administrator rights
so the overlay can see the Alt key (this can be turned off; see below). How to read and use the mini-radar is in the
**[guide](docs/ANLEITUNG.md)** (German). From version 0.8.0 the radar says so when a newer version exists and updates
itself on click ([Updates](docs/ANLEITUNG.md#8-updates)).

> **Warning:** According to another tool author, NCSOFT support treats software that reads game data as a violation
> of the terms of service. Use at your own risk.

## Layout

Status: **phase 3 (radar) is complete**. The Hidden Cube is identified in Verteron (templates 1200039 and 1200041,
send radius ≈ 100 m). Your own position comes from four event-driven sources. Everything learned so far is in
[`docs/FINDINGS.md`](docs/FINDINGS.md).

| Path | Contents |
|---|---|
| `src/CubeRadar.Core` | Capture (Npcap/SharpPcap), flow detection, TCP reassembly, framing/LZ4 (derived from cyberbadger6969/aion2-dps-meter, GPL-3.0), pcapng writer, JSONL dump, census |
| `src/CubeRadar.Core/Radar` | `RadarEngine` (cube status, own id/position, ring fix, template learning), SQLite store, spot index |
| `src/CubeRadar.Cli` | `cuberadar`: `radar`, `dump live`, `dump <file>`, `census`, `devices`, `conns`; markers via a global hotkey or the console; web UI in `wwwroot/` |
| `tests/CubeRadar.Tests` | xunit: synthetic framing tests, pcapng round trip, real stream fixtures |
| `tools/` | Python (stdlib only): `a2wire.py` (independent decoder), `census.py`, `crosscheck.py`, `make_fixture.py`, `objects.py` (object lifetime per template), `radius.py` (send radius), `selfscan.py` (f32 triplet scan for your own position), `selftrack.py` (own position track, CSV/SVG), `ringfix.py` (fallback estimate) |
| `config/opcodes.json` | Opcode names, including how well each one is confirmed, so a patch can be handled without a code change |
| `config/cubes.json` | Cube signature: templates per zone, opening item, measured send radius |
| `config/radar.json` | Radar opcodes, self-check, north axis, hold times, path to the optional spot list |
| `data/radar.db` | Your own spot database (SQLite, not in git) |
| `fixtures/` | Real stream excerpts (hex) with the frames they should produce |
| `captures/` | Recordings (not in git) |
| `ref/` | Reference repos (read only, not in git) |

## Build and test

```powershell
dotnet build CubeRadar.slnx -c Release
dotnet test CubeRadar.slnx -c Release
```

The executable is then at `src\CubeRadar.Cli\bin\Release\net10.0-windows\cuberadar.exe`. Short form: `.\cuberadar.cmd`.

## Publish a new version

GitHub builds and publishes the exe itself ([`.github/workflows/release.yml`](.github/workflows/release.yml)):

1. Raise `<Version>` in `src/CubeRadar.Cli/CubeRadar.Cli.csproj` and `src/CubeRadar.Core/CubeRadar.Core.csproj`,
   commit and push.
2. Create a tag with a description and push it:
   ```powershell
   git tag -a v0.8.1 -m "- What is new, one item per line"
   git push origin v0.8.1
   ```
   The tag description becomes “What's new in 0.8.1” in the release. Without a description, the release lists the
   commit subjects since the previous tag.
3. The Action checks that the tag matches the version, runs the tests, builds `Cube Radar.exe` and publishes the
   release with `Cube.Radar.exe` and its checksum. A running radar reports the update on the next start, or within
   6 hours.

`tools\package.ps1` builds the same exe locally. If it is running, it is asked to quit first via `--quit`.

## Radar

**As a single file to hand out:** `powershell -File tools\package.ps1` builds `dist\Cube Radar.exe`.
- The file contains .NET, the web UI and the default configuration. A double-click starts the overlay directly,
  with no console window.
- Data (recordings, database, settings, log) is stored in `%LOCALAPPDATA%\CubeRadar`.
- Your own config file in `%LOCALAPPDATA%\CubeRadar\config\` (for example `radar.json` with new opcodes after a
  patch) overrides the one built in.
- The program icon is produced by `tools\make_icon.ps1` (`assets\cuberadar.ico`).

From the repository:

```powershell
.\cuberadar.cmd radar      # full view: browser + overlay + console
.\overlay.cmd              # overlay only: no browser, no console, tray icon
```

**Overlay-only mode** (`overlay.cmd`, or `radar --overlay-only`):
- It runs invisibly in the background. The only thing on screen is the mini-radar over the game.
- Right-click the tray icon at the bottom right for: Settings (also a double-click), overlay on/off, zoom,
  “Open web view” and “Quit”.
- **Settings** open from the ⚙ button at the top right of the overlay, or from the tray icon. Changes apply
  immediately and are saved:
  - Sound on/off with “Test”, volume (you hear the sound while you move the slider), and an optional .wav file
    of your own. From startup, “Cube Radar” is its own app in the Windows volume mixer, separate from system sounds.
  - Zoom, size (75–150 %) and opacity, separately for the radar and for the enemy name list below it
  - North
  - “Only when Aion 2 is in front” and “Hide in menus”
  - Buttons: reset position, open the web view, quit the radar
- Output goes to `captures\radar_<date_time>\radar.log`. Recording, the database, the beep and the marker hotkeys
  keep working as usual.

The full view opens `http://127.0.0.1:5313` in the browser.
- **Radar** shows the area around you (north up, rings, view range ≈ 100 m as a dashed circle).
- **Height:** ▲ = cube above you, ▼ = below you, ■ = the same height (± 3 m). The height strip on the right of the
  radar uses your height as the centre line. The list says “▲ 38 m above you” and so on. If your position is older
  than 5 s, its age is shown with it.
- **Map** shows every cube, your trail and, when they exist, the possible spots of the zone.
- **List:** distance, compass direction, height difference, “seen ago”, “exists for” and status. The statuses are:
  visible, out of view (probably still there), being opened, opened (by you or by player N), gone.
- **“Sound on” and “Notifications on”** in the browser play a sound or raise a desktop notification for every new
  cube. The console beeps as well (turn that off with `--no-beep`).
- **Your own position** arrives only on events, for example when you cast a buff or when you are attacked. The
  header shows how old it is. Without a fresh position the radar shows an estimate with an uncertainty circle.
- **Mini-radar over the game (overlay):** a small window stays in front and never takes focus from the game.
  - Contents: north up, you in the centre, rings, view range (~100 m) dashed, and your recent trail. The trail
    shows where you came from, because the game does not send the direction you are facing.
  - Cubes appear as ▲/▼/■ (above, below, or at your height) with “86 m ▲38”. A cube outside the zoom is a »
    pointer on the rim, pointing toward it.
  - The header shows how old your position is, and the zoom.
  - **Move it:** `Ctrl+Shift+F11` opens Settings. While they are open the overlay stays visible and can be dragged
    with the mouse. The position is saved when you release it.
  - **Zoom:** `Ctrl+Shift+F12`, or the mouse wheel over the overlay while Settings are open (60 / 120 / 250 m).
  - **Shown and hidden on its own:** it is visible only while Aion 2 is the active window. While the mouse cursor
    is visible (menus, UI mode) it hides as well, even if the cursor stays where it is, so it never covers a menu.
    Exception: while you hold `Alt` (Aion 2 then shows the cursor) it stays visible until you let go.
    - It can see that `Alt` is held only with administrator rights. Aion 2 itself runs with them, and Windows then
      hides key state from programs that do not. So the overlay restarts itself once with administrator rights
      (a UAC prompt). If you decline, it keeps running without them, and `Alt` hides it the way a menu does. It
      still opens the browser and Explorer without administrator rights.
    - For this it reads only the Windows window, cursor and key state, nothing from the game.
    - With no game running (replay) it is always visible.
    - Turn off hiding in menus with `"OverlayHideInMenus": false` in `data\settings.json`, and turn off the
      administrator restart with `"OverlayAdminRights": false` (both are also in Settings).
  - `Ctrl+Shift+F10` turns it fully off and back on.
  - The game has to run windowed or borderless fullscreen. Nothing can draw over exclusive fullscreen.
  - Turn it off with `--no-overlay`.
- **North:** the configuration assumes world −Y (not confirmed). If the direction does not match the minimap,
  change it at the top under “North”. The choice also applies to the overlay and is saved in `data\settings.json`.
- **Recording:** every session writes `captures\radar_<date_time>\raw.pcapng` and `markers.jsonl`. The hotkeys in
  the next section work here too.
- **Database:** every sighting goes into `data\radar.db`. Export through the “CSV” and “JSON” links in the list.
- **Self-check:** if expected packets stop arriving (for example after a patch), a red notice appears. The opcodes
  are in `config\radar.json`.
- **Other zones:** when an object is opened with the cube key, or when it stands exactly on a known spot, the radar
  learns that template as a cube template.

Replay for testing: `.\cuberadar.cmd radar --replay captures\<session>\raw.pcapng --speed 5`. With `--stop-at 200`
the replay pauses at second 200 of the recording.

### Enemy faction and PvP/Abyss ranks (test)

From **0.9.0**. When moving from an older exe, download this version once by hand from this repository. Earlier
builds may still check the original repository for updates. From 0.9.0 the update check points here.

For Elyos: `.\cuberadar.cmd radar --overlay-only --faction Elyos`.
The choice is saved. Change it under **⚙ → Enemies → Own faction**, or in the web view under **“I play”**.
“Automatic” needs your own character packet, which can still be missing when the radar is started in the middle of
a session. For Asmodians use `--faction Asmodian`.

- Only players of the confidently recognised opposing faction appear as enemies. There is no guessing from entity
  or server ids.
- Red diamonds show fresh positions. Below them are the three nearest enemies, with faction and rank.
- From **0.9.1** the soldier class **1–9 is drawn inside the diamond**, **T** means Trainee and **?** means unknown.
  Name and distance sit beside it. The list keeps the full rank and the assumption star.
- 19 = Trainee, 17 = Eighth-Class Soldier, 16 = Seventh-Class Soldier, 15 = Sixth-Class Soldier and
  14 = Fifth-Class Soldier were seen on screenshots. The rest of the count
  (18 = Ninth-Class Soldier … 10 = First-Class Soldier; a smaller code is a higher rank) is an **assumption** (`*`).
- Unknown packet variants show **“rank unknown”**. No web profiles are queried.
- Position data can arrive late. After 10 s without a position update the live point disappears. The list marks old
  positions. Enemies who left or were killed, and contacts from before a teleport, are dropped.
- If your own position is old or estimated, directions and distances are uncertain too. The distance then carries
  `~`/`≈`.
- Under ⚙ the extra enemy display can be turned off. Cube tracking stays.

Rank mapping, when the code can be read from a supported packet variant:

| Code | PvP/Abyss rank | In the marker |
|---:|---|:---:|
| 19 | Trainee | T |
| 18 | Ninth-Class Soldier * | 9 |
| 17 | Eighth-Class Soldier | 8 |
| 16 | Seventh-Class Soldier | 7 |
| 15 | Sixth-Class Soldier | 6 |
| 14 | Fifth-Class Soldier | 5 |
| 13 | Fourth-Class Soldier * | 4 |
| 12 | Third-Class Soldier * | 3 |
| 11 | Second-Class Soldier * | 2 |
| 10 | First-Class Soldier * | 1 |

`*` = taken from the published counting order, not yet confirmed on its own screenshot.

## Capture

```powershell
.\cuberadar.cmd dump live              # until "q" + Enter, or Ctrl+C
.\cuberadar.cmd dump live --seconds 120
```

Each recording lands in `captures\<date_time>\`:
- `raw.pcapng`: raw packets for replays and Wireshark
- `frames.jsonl`: every decoded frame
- `markers.jsonl`: the markers you set
- `census.json` and `census.txt`: opcode census and stream health

**Markers** (a confirmation sound per key):

| Hotkey (global, works in game too) | Marker |
|---|---|
| Ctrl+Shift+F5 | `cube_visible` (cube visible) |
| Ctrl+Shift+F6 | `cube_opened` (cube opened) |
| Ctrl+Shift+F7 | `cube_out_of_sight` (cube out of sight) |
| Ctrl+Shift+F8 | `cube_back` (back in sight) |
| Ctrl+Shift+F9 | `note` (general marker) |

In the console, Enter sets a marker, optionally with text. `1`–`5` match the hotkeys, `q` ends the recording.
Bind other keys with `--hotkeys "ctrl+alt+F1=cube_visible,…"`.

## Analyse

```powershell
.\cuberadar.cmd dump captures\<session>\raw.pcapng     # replay -> raw.replay.jsonl (+ markers.jsonl is mixed in)
.\cuberadar.cmd census captures\<session>\raw.pcapng
python tools\census.py captures\<session>\frames.jsonl
python tools\crosscheck.py captures\<session>\raw.pcapng captures\<session>\frames.jsonl
```

### frames.jsonl

One JSON object per line. `t` is Unix milliseconds: the capture clock for packets, the wall clock for markers.

- `frame`: `n` (running number), `t`, `flow`, `src` (`top` | `lz4` | `emb`), `depth`, `path`, `op` (wire bytes as
  hex), `name`, `len` (body), `wire` (frame including the varint), `flag` (optional) and `hex` (body without the
  opcode).
  - `path` describes the nesting: `B3.0/B4.2` is the third frame in bundle 4, and bundle 4 is itself the first
    frame of bundle 3. `E7.0` is a bundle that was embedded in the body of another packet.
- `bundle`: `id`, `src`, `depth`, `path`, `host`, `raw`, `zlen`, `out`, `ok`
- `marker`: `t`, `local`, `label`, `text`, `via`
- also `session`, `flow`, `gap`, `stats`

## License

GPL-3.0 (see `LICENSE`), because parts are taken from cyberbadger6969/aion2-dps-meter. Derived files are marked in
their header comment. The project is meant for private use only. Using it may violate NCSOFT's terms of service;
the risk is yours.
