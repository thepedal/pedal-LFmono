# LF Mono — Low-Frequency Mono Effect for ReBuzz

Converts all stereo audio **below a chosen crossover frequency** to mono,
while leaving the high-frequency content fully stereo.  Typical use: keep
bass and sub bass centred (great for mastering / club mixes) while
preserving the stereo image of upper-mid and treble content.

---

## How it works

The machine implements a **4th-order Linkwitz-Riley (LR4) crossover**:

```
Input (stereo)
   │
   ├──► LP biquad ×2 ──► (L+R)/2 ──► mono LF ──► × LF Level ──┐
   │                                                             │
   └──► HP biquad ×2 ──────────── stereo HF ──► × HF Level ──► Sum ──► Output
```

Each "biquad ×2" is two cascaded 2nd-order Butterworth sections (Q = 1/√2).
Two LP stages → LR4 LP (−80 dB/oct, 0° phase at crossover).
Two HP stages → LR4 HP (−80 dB/oct, 180° phase at crossover, then LP+HP sums flat).

Coefficients are recomputed whenever the crossover frequency changes.
Filter state is flushed on every coefficient update to avoid transients.

---

## Parameters

| Parameter      | Range      | Default | Description                                      |
|----------------|------------|---------|--------------------------------------------------|
| Crossover Hz   | 40 – 800   | 230 Hz  | Frequencies below → mono; above → stereo         |
| LF Level %     | 0 – 200    | 100 %   | Output gain for the mono low band (100 = unity)  |
| HF Level %     | 0 – 200    | 100 %   | Output gain for the stereo high band             |

---

## Building

1. Copy `BuzzGUI.Interfaces.dll` and `BuzzGUI.Common.dll` from your ReBuzz
   installation into a `lib\` folder next to the `.csproj`.
2. Open a terminal in the project folder:
   ```
   dotnet build -c Release
   ```
3. Copy the resulting `LFMono.dll` to:
   ```
   <ReBuzz install>\Gear\Effects\LFMono\LFMono.dll
   ```
4. (Re)start ReBuzz.  The machine appears as **LF Mono** under *Effects*.

> **Tip**: set `<OutputPath>` in the `.csproj` to your Gear folder so the
> DLL is copied automatically on every build.

---

## Notes

* The machine is marked `STEREO_EFFECT`, so ReBuzz will always pass stereo
  buffers regardless of the upstream connection type.
* Changing Crossover Hz while audio is running causes a brief mute because
  filter state is flushed.  This is intentional to prevent clicks from
  discontinuous state; automate carefully at zero-crossings if needed.
* The `Save`/`Load` methods store all three parameter values so presets are
  preserved across sessions.
