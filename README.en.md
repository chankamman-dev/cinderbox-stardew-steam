<h1 align="center">Stardew Valley in Cinderbox · Steam presence &amp; faster loading</h1>

<p align="center">English · <a href="README.md">中文</a></p>

<p align="center">
Two SMAPI mods for people running the PC version of Stardew Valley on Android through Cinderbox.<br>
One lets your Steam friends see you playing and writes achievements back to your account; the other cuts the load time of a heavily modded save in half.
</p>

<p align="center">
  <img alt="SMAPI 4.x" src="https://img.shields.io/badge/SMAPI-4.x-7bb241?style=flat-square">
  <img alt="Stardew Valley 1.6.15" src="https://img.shields.io/badge/Stardew%20Valley-1.6.15-f2a73b?style=flat-square">
  <img alt="Cinderbox 0.8" src="https://img.shields.io/badge/Cinderbox-0.8.x-6c5ce7?style=flat-square">
  <img alt="Android arm64" src="https://img.shields.io/badge/Android-arm64-3ddc84?style=flat-square">
</p>

---

## Why these exist

I play Stardew on an AYN Thor through Cinderbox, with sixty-odd mods and seventy-odd content packs (the SVE / Ridgeside / East Scarp crowd). It runs fine, but two things kept bugging me:

1. Cinderbox replaces the game's Steamworks with an empty stub, so Steam never shows "now playing", playtime doesn't count, and achievements earned in-game never reach the account.
2. Loading a save took close to fifty seconds.

The first turned out to be easier than expected: Cinderbox ships a complete copy of SteamKit2 (it uses it to sign in and download the game), so there is nothing to reinvent — the mod borrows it from inside the same process. The second came out of measuring: well over half of the load time went into decoding the same PNGs over and over and parsing XML maps, and both of those can be cached.

The two mods are independent; install one or both.

## SteamPresence — let Steam know you're playing

<table>
<tr><td width="150"><b>Presence</b></td><td>While the game is in the foreground, friends see "Playing Stardew Valley". Switching to the home screen or another app takes you offline; coming back brings you online again. Tapping the lower screen doesn't count as leaving.</td></tr>
<tr><td><b>Playtime</b></td><td>Counted by Steam exactly as with the desktop client.</td></tr>
<tr><td><b>Achievements</b></td><td>Written to your account the moment the game grants them. After a save loads, the game re-reports every achievement the save already holds (the PC version does the same), so achievements you earned long ago but never got on Steam arrive on the first load.</td></tr>
<tr><td><b>Unlock toast</b></td><td>A Steam-style card rises in the bottom-right corner with the icon and name, stays five seconds and drops back. It only appears after Steam confirms the write; achievements the account already has never produce one, and a large first sync collapses into a single card.</td></tr>
<tr><td><b>Sign-in</b></td><td>Uses the Steam account you signed in with in Cinderbox's launcher, with nothing to do. If there isn't one, a QR code appears on the title screen; scan it once with the Steam mobile app and it's remembered.</td></tr>
<tr><td><b>Plays nice</b></td><td>If the same account is playing on another device, this one stays quiet — the same behaviour as the Steam client.</td></tr>
</table>

### How it works

- Cinderbox, the game, SMAPI and every mod share one Mono process, and so does SteamKit2. The mod opens a session of its own on it (a separate LoginID, so it never fights Cinderbox's download sessions) and sends `ClientGamesPlayed` once online.
- Credentials are read-only: Cinderbox's own credential class is called through reflection for the account name and refresh token; nothing is written, refreshed or deleted. A token obtained by QR login lives in the app's private directory, encrypted with the mod's own AndroidKeyStore key, separate from Cinderbox's.
- Achievements go through Harmony: `Game1.getPlatformAchievement` is intercepted, the Steam stats schema is fetched once, the bits that aren't set yet are OR-ed in and written back, and the mod waits for Steam's acknowledgement.
- Steam is reached over WebSocket only. Plain TCP to the CM servers tends to drop when a proxy is running on the phone — a lesson from earlier ports.

## LoadCache — loading in half the time

The result first. Same save, same mods, on the Thor:

| | before | after |
|---|---:|---:|
| Save parsed → first day starts | 46.3 s | **24.3 s** |
| of which: PNG decoding | 18.8 s | 2.5 s |
| of which: TMX map parsing | 9.8 s | 4.5 s |
| Full GCs during the load | 23 | 3 |

### Where the time went

A small timing-only mod measured one load: 2,400-odd image decodes and 200-odd TMX maps. The absurd part was the repetition — Content Patcher flushes its cache a few times during a load and many patches share one source image, so a single `Cursors_Fall.png` got decoded 34 times and a small tilesheet 194 times. Cinderbox has a switch for an image cache, but it's off by default, and once on it keeps every decoded image in memory for good.

### Three things

**No repeated decoding during a load.** Decoded images stay in memory and are handed out again when the same file is requested. It's a byte-capped LRU, emptied a few seconds after the day starts, so nothing lingers while you play.

**PNGs decoded straight into the game's format.** SMAPI's path is Skia decode → premultiply alpha → a per-pixel loop converting into MonoGame's `Color`; on Mono that last loop costs more than the decode itself. Now Skia writes directly into the destination array — no intermediate buffer, no conversion loop. The output is byte-for-byte identical (checked against real mod files).

**TMX maps cached as TBIN.** Nearly all mod maps are TMX (Tiled's XML), which parses about four times slower than xTile's binary TBIN. The first time a map is parsed it's stored as TBIN, read straight back and compared with the original layer by layer, tile by tile, property by property; only an identical round-trip is kept. From then on the TBIN is loaded as long as the TMX hasn't changed. Maps the binary format can't carry are marked and parsed normally.

None of this changes what actually gets loaded — Content Patcher's edits run as before, and caches invalidate themselves when a mod updates. Delete the mod folder and everything is back to stock.

## Install

1. Grab `SteamPresence-x.y.z.zip` and/or `LoadCache-x.y.z.zip` from Releases.
2. Unzip and drop the folders into `/sdcard/StardewValley/desktop/Mods/`.
3. Restart the game.

The first time SteamPresence runs without a Steam sign-in in Cinderbox, the title screen shows a QR code; press B to dismiss it for this session if you'd rather not scan now.

LoadCache builds its map cache on the first load (under `Mods/LoadCache/cache`, a few dozen MB), which makes that one load slightly slower; the speed-up starts with the second.

## Configuration

Both mods write a `config.json` into their own folder on first run.

**SteamPresence**

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch |
| `Achievements` | `true` | Write achievements to Steam |
| `AchievementToasts` | `true` | Show the unlock card |
| `ToastTestOnStart` | `false` | Show a test card at startup to check the look; doesn't touch Steam |

**LoadCache**

| Key | Default | Meaning |
|---|---|---|
| `ImageCache` | `true` | Per-load image dedupe |
| `ImageCacheMB` | `256` | Memory cap for that cache; drop to 128 on a tight device |
| `ReleaseAfterSeconds` | `5` | Seconds after the day starts before the image cache is emptied |
| `CollectAfterRelease` | `false` | Force a GC after emptying. Returns memory at once, at the cost of a half-second stall each morning |
| `FastDecode` | `true` | Direct PNG decoding |
| `MapCache` | `true` | TMX → TBIN map cache |
| `LogStats` | `true` | Print a statistics block to the SMAPI log after each load |

Console commands: `steam_presence` (status), `steam_login` (show the QR code again), `steam_logout` (forget the mod's own QR login), `steam_toast_test`, `load_cache` (cache statistics), `load_cache_clear`.

## Building from source

Each mod has two layers:

- The main layer is an ordinary SMAPI mod (net6.0) referencing the PC game and SMAPI.
- The `*.Core` layer is compiled against the .NET 10 assemblies extracted from Cinderbox's APK — SteamKit2, SkiaSharp, Mono.Android — and loaded by the main layer at run time with `Assembly.LoadFrom`. The detour exists because SMAPI's assembly rewriter only resolves assemblies it can find on disk, and these live inside the APK's `libassembly-store.so` (`tools/extract_store.py` unpacks it).

You need the .NET 8 SDK, your own PC copy of Stardew Valley 1.6.15 with SMAPI, and the extracted assembly directory. Copy `local.props.example` to `local.props` and fill in the two paths (`GamePath`, `AsmDir`), or set the `STARDEW_GAME_PATH` and `CINDERBOX_ASM_DIR` environment variables.

```bash
bash tools/build.sh         # SteamPresence → out/SteamPresence
bash tools/build-cache.sh   # LoadCache     → out/LoadCache
```

`tests/` holds three things: `CoreSmoke` smoke-tests the Steam connection and QR encoder on desktop .NET 10; `CacheCheck` verifies map round-trips and byte-equal PNG decoding against real mod files; and two load-timing reports from the Thor, before and after the cache.

The details, the dead ends and what changed in each version are in [HANDOFF.md](HANDOFF.md) (Chinese).

## Known limits

- Cinderbox only. Under desktop SMAPI the mods do nothing (LoadCache can be forced on with `AllowOutsideCinderbox`; SteamPresence can't — you have a real Steam client there).
- If a Cinderbox update renames its credential class, SteamPresence falls back to QR login; if it stops bundling SteamKit2, the mod stops working (unlikely — Cinderbox needs it to download the game).
- A TMX that pulls in an external `.tsx` tileset is only re-cached when the `.tmx` itself changes.
- No cloud saves. Syncing a save with a hundred mods against a PC save is asking for one to overwrite the other; that stays manual.
- The forty-odd seconds from launch to the title screen are mostly Content Patcher initialising a hundred-plus content packs. Nothing here helps with that.

## What's not in the repo

No game files, no Cinderbox files, no mod assets, no extracted assemblies. Just the two mods' source, build scripts and tests.
