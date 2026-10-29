# GPS HUD for GTA IV

**Stop squinting at the minimap.** This mod paints your GPS route right onto the streets of Liberty City:
glowing arrows on the road ahead that follow the game's own GPS line through every turn, hill and
highway ramp.

- 🧭 **Follows the real GPS route.** It's read straight from the game, so it matches the minimap exactly.
- 🛣️ **Arrows that belong in the world.** The game's own route-arrow props lie flat on the asphalt and
  tilt with the slope. Cars and buildings hide them, and they never lag behind the camera.
- 🚗 **Road vehicles only.** The arrows hide while you're in a boat, plane, helicopter or train.
- ➡️ **Tighter arrows through bends.** Arrows close up in curves, so the chain follows the corner
  instead of jumping across it, while straights keep the normal spacing.
- 🔦 **A soft orange glow** on the nearest arrows, so they stand out at night.
- 🏎️ **Looks further ahead the faster you drive**, so you see the turn before you miss it.
- ✨ **Fades in and out smoothly** as you drive along the route.
- ⌨️ **F7** toggles the HUD. Everything else can be changed in `GPSHud.ini`.

Set a waypoint (or start a mission) and drive.

## Install

1. Install [ScriptHookDotNet 1.7.1.8](https://github.com/Tomasak/gta4_scripthookdotnet/releases) and an
   ASI loader (FusionFix already includes one).
2. Download the latest zip from [Releases](https://github.com/garuspl/gta4-gps-hud/releases) and extract
   it into your `GTAIV` folder, so `GPSHud.net.dll` and `GPSHud.ini` end up in `GTAIV\scripts`.

Requires **GTA IV Complete Edition 1.2.0.59**. Other game versions are not supported (the GPS route is
read from that version's memory layout).

## Building from source

Extract ScriptHookDotNet into `shdn/`, then:
```
powershell -File build.ps1
powershell -File install.ps1 -GameDir "<path to GTAIV folder>"
```
No SDK is needed; the build uses the C# compiler that ships with .NET Framework 4.x. Pushing a tag such
as `v1.0.0` makes GitHub Actions build the mod and publish a release. It's also uploaded to Nexus Mods as
a new version of the mod's file once the repository has a `NEXUS_API_KEY` secret and a
`NEXUSMODS_FILE_ID` variable.

## Troubleshooting

- Set `ShowDebug=true` in `GPSHud.ini` to show the route line, the route source and timing numbers.
- If performance drops, lower `GlowMaxLights` (0 turns the lights off).
- Errors are written to `ScriptHookDotNet.log` in the game folder.
