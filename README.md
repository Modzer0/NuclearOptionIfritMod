# NuclearOptionIfritMod

A BepInEx mod for [Nuclear Option](https://store.steampowered.com/app/2230000/Nuclear_Option/) that creates the **KR-67X SuperIfrit** — a scramjet-capable, hypersonic variant of the KR-67A Ifrit multirole fighter.

## Features

### KR-67X SuperIfrit Aircraft
- Cloned from the KR-67A Ifrit with its own encyclopedia entry, hangar slot, and mission editor support
- Doubled turbojet thrust (200kN max, 94kN afterburner) with flat altitude curve
- Scramjet engines activate above Mach 4.5 and 60,000 ft, providing 500kN per engine
- Engine flameout above 164,000 ft with automatic relight on descent
- Max speed raised to 4,500 m/s (~Mach 15.5 at altitude)
- Overspeed warnings suppressed; speed gauge updated for new limits
- HUD indicator with pulsing "SCRAMJET ACTIVE" text

### Hypersonic Flight Stability
- Relaxed stability controller dampening above Mach 2 (canard range scaling)
- Fly-by-wire pitch/roll dampening above Mach 3 to prevent oscillations
- Automatic flap retraction above Mach 1 to prevent deployment damage
- Reinforced airbrakes survive deployment at Mach 5 at sea level

### Radar
- Doubled radar range and signal strength on the KR-67X

### Configurable Options
- **Darkstar Mode** — Doubles scramjet thrust with a 10-second ramp-up (off by default)
- **Scimitar Thrust** — Override AAM-36 Scimitar missile motor thrust in kN, with auto-scaled guidance torque and turn rate (0 = stock, off by default)

### Mission Editor
- KR-67X is injected into the mission editor unit list automatically

## Installation

1. Install [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) into your Nuclear Option game directory
2. Copy `NuclearOptionIfritMod.dll` into `BepInEx/plugins/`
3. Launch the game — config file generates at `BepInEx/config/com.custom.ifritmod.cfg`

## Configuration

Edit `BepInEx/config/com.custom.ifritmod.cfg`:

```ini
[General]
# Double scramjet thrust when enabled.
Darkstar Mode = false

[Weapons]
# Override Scimitar motor thrust in kN. 0 = stock.
Scimitar Thrust kN = 0
```

## Building from Source

Requires .NET Framework 4.7.2 and game DLLs from a Nuclear Option install.

1. Update `GameDir` in `NuclearOptionIfritMod.csproj` to point to your Nuclear Option install
2. Build:
   ```
   dotnet build NuclearOptionIfritMod/NuclearOptionIfritMod.csproj -c Release
   ```
3. The DLL is automatically copied to `BepInEx/plugins/` by the post-build target

## Dependencies

- [BepInEx 5.x](https://github.com/BepInEx/BepInEx)
- [Nuclear Option](https://store.steampowered.com/app/2230000/Nuclear_Option/)
- Optional soft dependency on QoL mod (`com.offiry.qol`)

## License

This project is licensed under the GNU General Public License v3.0. See [LICENSE](LICENSE) for details.
