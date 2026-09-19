# SimplePlanes 2 Rangefinder Plugin

[中文](README.md)

Uses refuel probe parts as laser rangefinders and presence sensors. Their output is published as Funky Trees variables that any part on the aircraft can reference.

## Installation

### Installing BepInEx

The release package does not bundle BepInEx. If you already have BepInEx 5 set up, skip to "Installing this plugin".

This plugin is tested on BepInEx 5.4.23.5 (Mono x64); use a [BepInEx 5.4.x build](https://github.com/BepInEx/BepInEx/releases). Download [BepInEx_win_x64_5.4.23.5.zip](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip) and extract it into the game root, the folder containing `SimplePlanes 2.exe`:

```text
SimplePlanes 2\
├─ winhttp.dll
├─ doorstop_config.ini
├─ .doorstop_version
└─ BepInEx\
```

Launch the game once and quit; `BepInEx\plugins\` and `BepInEx\config\` are created automatically.

### Installing this plugin

Put `SimplePlanes2Rangefinder.dll` in any subfolder of `BepInEx\plugins\`; the convention is one folder per plugin:

```text
SimplePlanes 2\
└─ BepInEx\
   └─ plugins\
      └─ SimplePlanes2Rangefinder\
         └─ SimplePlanes2Rangefinder.dll
```

The game must be closed while installing: the DLL is locked while it runs. The config file is created on first launch:

```text
BepInEx\config\com.codex.simpleplanes2.rangefinder.cfg
```

## Configuration

| Key | Default | Effect |
|---|---|---|
| `General / Enabled` | `true` | Master switch. While off, values are not updated and every registered variable is set to its own no-hit value. |
| `General / PartNamePrefix` | `RF_` | Prefix of a rangefinder part name, case-sensitive. Empty means no part is treated as a rangefinder. |
| `General / SensorNamePrefix` | `SN_` | Prefix of a sensor part name, case-sensitive. When it equals the rangefinder prefix, such parts are treated as sensors. Empty means no part is treated as a sensor. |
| `General / PartTypeId` | `Refuel-Probe-1` | Part type ID that is treated as a rangefinder or a sensor. |
| `General / LogLevel` | `Normal` | Log verbosity. `Off` emits no plugin log output at all; `Normal` logs loading, registration, failures and warnings; `Verbose` additionally logs each part's recognition and thread information. |
| `Rangefinder / DefaultRefreshHz` | `10` | Refresh rate in Hz when the part name has no `r` parameter. |
| `Rangefinder / DefaultMaxDistance` | `100000` | Ray length in metres when the part name has no `m` parameter. |
| `Rangefinder / IgnoreOwnAircraft` | `true` | Whether own-aircraft collision boxes are hidden when the part name has no `c` parameter. |
| `Rangefinder / OnlyUpdateLocalPlayer` | `false` | Update only the local player's aircraft. Skipped aircraft have their variables set to their own no-hit values. |

---

## Rangefinder

In the designer, select a refuel probe part and set its **part name** in the part properties panel, starting with `RF_`:

```text
RF_left
```

The part type must be `Refuel-Probe-1` (configurable through `PartTypeId`). Renaming a part of another type only gets the part skipped, with a type warning in the log.

The variable name is the part of the part name before the first `.` (prefix included), with surrounding spaces removed. It may only contain letters, digits and underscores, and may not start with a digit; otherwise the part is skipped and a warning is logged. Prefix matching is case-sensitive. A part name that is only a prefix (`RF_` or `RF_.r5`) gets the whole part skipped and a warning logged.

The output is the distance from the probe to whatever it hits, in metres. It is about 0.01 metres at short range; at long range floating-point precision is coarser than that.

### Parameters

The part name may carry `.`-separated parameters:

```text
RF_left.r5.m500.c1
```

| Parameter | Meaning | Range | When omitted |
|---|---|---|---|
| `r<n>` | measurements per second | 0.1 – 120 | 10 per second |
| `m<n>` | ray length, metres | 1 – 1000000 | 100000 metres |
| `c<0\|1>` | whether to hide own-aircraft collision boxes | 0 or 1 | 1, hidden |

Parameter letters are case-insensitive. A value outside the range is replaced by the nearest bound for `r` and `m`, so `r500` is treated as 120; `c` accepts only 0 and 1, and any other value is logged as a warning and ignored, leaving the omitted behaviour in place. Parameters may all be omitted, partially omitted, or written in any order; when the same parameter is written twice, the last one wins. An empty parameter segment and an unrecognised parameter are each logged as a warning and ignored, leaving the rest of the rangefinder unaffected. `t` only applies to sensors; on a rangefinder it is ignored with a warning.

### Examples

| Part name | Effect |
|---|---|
| `RF_left` | 10 per second, 100000 m ray, own-aircraft collision boxes hidden |
| `RF_left.r2` | 2 per second |
| `RF_left.m1000` | 1000 m ray |
| `RF_left.c0` | Own-aircraft collision boxes not hidden |
| `RF_left.m500.r5` | Same as `RF_left.r5.m500`; order does not matter |
| `RF_left.r5.m500.c1` | All three parameters written out |

### Output values

| Case | Value |
|---|---|
| Ray hits | distance in metres |
| Nothing within the ray length | the ray length |

A hit exactly at the ray length boundary and nothing at all produce the same value and cannot be told apart.

### Referencing it in an expression

The variable name is the part of the part name before the first `.`: the part name `RF_left.r5.m500` gives the variable name `RF_left`.

In the designer, Ctrl+click any part's input field (control surface, rotor, piston, and so on) and enter the expression. For example, an elevator input:

```text
clamp01(1 - RF_left / 500)
```

That elevator's deflection becomes: full deflection at 0 metres, zero at 500 metres, linear in between.

Write only the variable name in expressions; the parameter suffix is not repeated.

### Ray direction

The ray starts at the part's transform position and runs along the part's local +Y (`transform.up`), which is the direction the probe's rounded tip points. The part's orientation in the designer determines the ray direction. When the probe is attached to a rotating part, the ray rotates with it.

### Multiple rangefinders

Give each probe a different part name. Each refreshes and ranges independently:

```text
RF_front
RF_left
RF_right
```

### Scope

Variables belong to the aircraft they are on. A probe on one aircraft only affects that aircraft's expressions.

---

## Sensor

In the designer, select a refuel probe part and set its **part name** in the part properties panel, starting with `SN_`:

```text
SN_beam
```

The part type must be `Refuel-Probe-1` (configurable through `PartTypeId`). Renaming a part of another type only gets the part skipped, with a type warning in the log.

The variable name is the part of the part name before the first `.` (prefix included), with surrounding spaces removed. It may only contain letters, digits and underscores, and may not start with a digit; otherwise the part is skipped and a warning is logged. Prefix matching is case-sensitive. A part name that is only a prefix (`SN_` or `SN_.r5`) gets the whole part skipped and a warning logged.

A sensor outputs clearance, and has two output modes:

| Mode | Spelling | Nothing in the ray | Something in the ray |
|---|---|---|---|
| Binary | default | 1 | 0 |
| Linear | add `t2` | 1 | `distance / ray length`, near 1 far away, falling to 0 up close |

Binary mode answers whether the path is clear; linear mode answers how clear it is.

### Parameters

The part name may carry `.`-separated parameters:

```text
SN_beam.t2.m50
```

| Parameter | Meaning | Range | When omitted |
|---|---|---|---|
| `r<n>` | measurements per second | 0.1 – 120 | 10 per second |
| `m<n>` | ray length, metres | 1 – 1000000 | 100000 metres |
| `c<0\|1>` | whether to hide own-aircraft collision boxes | 0 or 1 | 1, hidden |
| `t<1\|2>` | output mode | 1 binary, 2 linear | 1 |

Parameter letters are case-insensitive. A value outside the range is replaced by the nearest bound for `r` and `m`, so `r500` is treated as 120; `c` accepts only 0 and 1 and `t` only 1 and 2, and any other value is logged as a warning and ignored, leaving the omitted behaviour in place. Parameters may all be omitted, partially omitted, or written in any order; when the same parameter is written twice, the last one wins. An empty parameter segment and an unrecognised parameter are each logged as a warning and ignored, leaving the rest of the sensor unaffected.

### Examples

| Part name | Effect |
|---|---|
| `SN_beam` | Binary output, 10 per second, 100000 m ray |
| `SN_beam.m20` | Outputs 0 when something is within 20 m, 1 otherwise |
| `SN_beam.t2.m50` | Linear output; within 50 m it is near 1 far away and falls to 0 up close; 1 when nothing is there |
| `SN_beam.c0` | Own-aircraft collision boxes not hidden |
| `SN_beam.r2.m20` | 2 per second, 20 m ray, binary output |

### Output values

| Case | Binary | Linear |
|---|---|---|
| Nothing in the ray | 1 | 1 |
| Something in the ray | 0 | `distance / ray length` |

In binary mode anything in the ray gives 0, so every hit can be told apart from nothing being there. In linear mode a hit exactly at the ray length boundary and nothing at all produce the same value and cannot be told apart.

### Referencing it in an expression

The variable name is the part of the part name before the first `.`: the part name `SN_beam.t2.m50` gives the variable name `SN_beam`.

In the designer, Ctrl+click any part's input field (control surface, rotor, piston, and so on) and enter the expression. In binary mode it can be used directly as a switch:

```text
SN_beam
```

In linear mode it is a continuous value. Invert it to get a warning that grows as the gap closes:

```text
1 - SN_beam
```

Write only the variable name in expressions; the parameter suffix is not repeated.

### Ray direction

The ray starts at the part's transform position and runs along the part's local +Y (`transform.up`), which is the direction the probe's rounded tip points. The part's orientation in the designer determines the ray direction. When the probe is attached to a rotating part, the ray rotates with it.

### Multiple sensors

Give each probe a different part name. Each refreshes and ranges independently:

```text
SN_left
SN_right
SN_beam
```

### Scope

Variables belong to the aircraft they are on. A probe on one aircraft only affects that aircraft's expressions.

---

## Uninstallation

Delete the `BepInEx\plugins\SimplePlanes2Rangefinder\` directory. The config file may be deleted as well.
