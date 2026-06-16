# BibiControl — headless + IPC mod for The Bibites

Adds two things to The Bibites:

- **Headless launch** — a `-bibiteHeadless` flag that boots straight into the
  simulation with a chosen save, with no menus and (combined with Unity's
  `-batchmode -nographics`) no window.
- **An IPC server** — a TCP server speaking the same newline-delimited-JSON
  envelope protocol as the Go `ipc` package, implementing `STOP`, `RESUME`,
  `INFO`, `RELOAD`, and `NEWSAVE`. The control plane (`simctl`) dials in and
  drives it.
- **Seed minting** — mint a fresh, engine-made save from any scenario *without
  the GUI*, either one-shot (`-bibiteMintSeed`) or live over IPC (`NEWSAVE`). Lets
  you generate worlds programmatically instead of hand-building a scenario
  in-game (a scenario is a strict subset of a save).

No BepInEx / Doorstop. It is pure managed C#, so it runs on every platform the
game's managed assembly runs on (incl. macOS/ARM).

---

## Status — verified working end-to-end

Tested on a live install (The Bibites, Unity **6000.0.44f1**, Mono, Steam):

| Piece | Status |
|-------|--------|
| Go control client (`simctl`) | ✅ built + unit-tested (`go test -race`) |
| Mod C# code | ✅ compiles cleanly against the real game assemblies |
| Headless launch | ✅ runs windowless (`-batchmode -nographics`), loads the given save |
| Mod running in-game (IPC server up) | ✅ `[BibiControl] IPC listening on 127.0.0.1:43100` |
| `STOP` / `RESUME` / `INFO` / `RELOAD` | ✅ all answered correctly over the network |
| `NEWSAVE` / `-bibiteMintSeed` (seed minting) | ✅ verified end-to-end: minted an empty seed headless (exit 0, valid `.zip`), then round-tripped it — booted `-bibiteSave`, sim runs at 40 TPS with zero exceptions |

Observed in a real run: `INFO` → `{"tps":15,"real_tps":15.0,"paused":false,...}`,
`STOP` → `{"previous_time_scale":1}` then `paused:true`, `RESUME` at `5` then
`paused:false`, `RELOAD` → reloaded the newest autosave.

**Loading note (important).** On this Unity 6 build, dropping `BibiControl.dll`
into `Managed` and listing it in `ScriptingAssemblies.json` does **not** run the
mod (Unity never loads an assembly nothing references). The working approach is
the **IL-patch loader** below, which injects a call to the mod into the game's
own `AppInitializer.Awake()`.

---

## Files

| File | Purpose |
|------|---------|
| `BibiControl/HeadlessController.cs` | Arg parsing, server bootstrap (`[RuntimeInitializeOnLoadMethod]`), headless redirect |
| `BibiControl/IpcServer.cs` | TCP server, envelope framing, main-thread dispatch, command registry |
| `BibiControl/SimCommands.cs` | `STOP` / `RESUME` / `INFO` / `RELOAD` / `NEWSAVE` handlers |
| `BibiControl/SeedMinter.cs` | Mints a save from a scenario; shared by `-bibiteMintSeed` and `NEWSAVE` |
| `BibiControl/Envelope.cs` | JSON envelope DTO (mirrors `ipc.Envelope`) |
| `BibiControl.csproj` | Builds the mod as a DLL |
| `patcher/` | Mono.Cecil tool that injects the mod into the game's assembly (the proven loader) |

---

## Building

### Compile-check against your game (easy, no install)

`BibiControl.csproj` builds the four files as a standalone DLL referencing the
shipped game assemblies. This proves the code matches your game's API:

```
dotnet build dll/BibiControl.csproj -c Release `
  -p:GameManaged="C:\Program Files (x86)\Steam\steamapps\common\The Bibites\The Bibites_Data\Managed"
```

(Default `GameManaged` already points at the typical Steam path.) This produces
`BibiControl.dll`, but on Unity 6 that standalone DLL will **not auto-load** (see
Status). It's a build/lint check, not the deployable artifact.

### Install via the IL-patch loader (recommended, proven)

`dll/patcher` is a tiny Mono.Cecil tool that injects a call to
`BibiControl.HeadlessController.Bootstrap()` into the shipped game's
`AppInitializer.Awake()` and adds the assembly reference — so the game loads and
runs the mod **without recompiling its source**. This is what was verified above.

Let `MGD = …\The Bibites\The Bibites_Data\Managed`.

```powershell
# 1. Build the mod DLL
dotnet build dll/BibiControl.csproj -c Release -o out

# 2. Patch a copy of the game's assembly (out\BibitesAssembly.patched.dll)
dotnet run --project dll/patcher -c Release -- `
  "$MGD\BibitesAssembly.dll" "out\BibiControl.dll" "out\BibitesAssembly.patched.dll"

# 3. Install (back up the original first!)
Copy-Item "$MGD\BibitesAssembly.dll" "$MGD\BibitesAssembly.dll.bak"
Copy-Item "out\BibitesAssembly.patched.dll" "$MGD\BibitesAssembly.dll" -Force
Copy-Item "out\BibiControl.dll" "$MGD\BibiControl.dll" -Force
```

The patcher is idempotent (re-running on a patched assembly is a no-op). To
uninstall, restore `BibitesAssembly.dll.bak` and delete `BibiControl.dll`.

> Re-apply after a game update (a patch overwrites `BibitesAssembly.dll`).

### Alternative: bake into `BibitesAssembly`

If you have the game's decompiled, recompilable source, copy `dll/BibiControl/`
into it and rebuild `BibitesAssembly.dll`. This avoids the patch step but means
recompiling the full ~94k-line assembly, which needs the right toolchain and may
need small decompiler-artifact fixes — heavier than the IL-patch above.

---

## Running (Steam gotchas matter)

Launching The Bibites' `.exe` directly makes Steam relaunch it through Steam and
**strip your command-line args** (only `ARG 0` reaches the game). Two fixes:

- **Add `steam_appid.txt`** containing `2736860` next to `The Bibites.exe`. Then a
  direct launch keeps your args. (Confirmed working.)
- **or** set the args as **Steam launch options** and start it from Steam.

Then launch headless (note: **quote save paths that contain spaces** — the Bibites
save folder usually does):

```
"…\The Bibites.exe" -batchmode -nographics ^
  -bibiteHeadless ^
  -bibiteSave "C:\Users\<you>\AppData\LocalLow\The Bibites\The Bibites\Savefiles\<world>\saves\_00.zip" ^
  -bibiteIpcPort 43100
```

Confirm it loaded by checking `Player.log`
(`…\AppData\LocalLow\The Bibites\The Bibites\Player.log`, or pass `-logFile <path>`)
for the line:

```
[BibiControl] mod loaded (headless=True)
```

Flags:

| Flag | Default | Meaning |
|------|---------|---------|
| `-bibiteHeadless` | off | Enable headless mode + start the IPC server |
| `-bibiteSave <path\|latest>` | (none) | Save to auto-load; `latest` = newest save the game finds (quote if it has spaces) |
| `-bibiteIpcPort <port>` | `43100` | TCP listen port |
| `-bibiteIpcHost <host>` | `127.0.0.1` | Bind host (`0.0.0.0` to listen on all interfaces) |
| `-bibiteMintSeed` | off | One-shot: mint a seed save from a scenario, then quit |
| `-bibiteScenario <path\|default>` | `default` | Scenario to mint from (absolute `.zip`, or the bundled default) |
| `-bibiteOut <path>` | (none) | Destination `.zip` for the minted save (required with `-bibiteMintSeed`) |

---

## Minting a seed save

The Go tooling *mutates* save files but can't create one from scratch — so you
need one engine-made seed to build on. This mints one from a scenario (which is a
strict subset of a save), with no GUI. Two ways, identical output:

**One-shot (launch → write → exit):**

```
"…\The Bibites.exe" -batchmode -nographics ^
  -bibiteMintSeed ^
  -bibiteScenario default ^
  -bibiteOut "C:\seeds\seed.zip"
```

It boots, starts a new game from the scenario, saves to `-bibiteOut`, and quits
(exit `0` on success, `1` on failure). `-bibiteScenario` takes an absolute
scenario `.zip`, or `default` for the bundled Default scenario. The
`steam_appid.txt` / launch-option gotchas above still apply.

**Live, over IPC (`NEWSAVE`):** boot headless with the IPC server and *no*
`-bibiteSave` (so it idles in the menu), then drive it:

```go
sim := simctl.New(sess)
res, _ := sim.NewSave(ctx, "default", `C:\seeds\seed.zip`)  // res.Path = file written
```

The minted seed is empty (scenario rules + zones, but no bibites and no initial
pellets) — purely additive ground for the save mutator.

> Note: the screenshot step in the game's own save path is a no-op under
> `-nographics` (it writes a blank `img.png`), so **no patching of the game's save
> code is required** — `NEWSAVE` reuses `SaveSystem.SaveGame` as-is.
>
> Both minting and loading a save headless first wait for the game's procedural
> sprite atlas (it loads lazily in the menu and persists into the sim). Otherwise
> new-game pellet seeding and loaded entities index it before it's populated and
> throw `IndexOutOfRange` under `-nographics`.

## Testing it (end to end)

1. Build + install the mod (bake-in path above) and add `steam_appid.txt`.
2. Launch headless with a quoted save path on port 43100.
3. From the repo, run the manual client and watch it exercise the commands:
   ```
   go run ./cmd/livetest 127.0.0.1:43100
   ```
   It waits for the world to load, then prints `INFO`, `STOP`, `INFO`,
   `RESUME x5`, `INFO`, `RELOAD`, `INFO`. (For the automated protocol test that
   needs no game, run `go test ./simctl/...`.)

---

## Driving it from Go

The control plane is the TCP client and uses `simctl`:

```go
sess, _ := ipc.Dial(ctx, "127.0.0.1:43100", nil)
sim := simctl.New(sess)

prev, _ := sim.Stop(ctx)                       // pause; prev.PreviousTimeScale = old speed
info, _ := sim.Info(ctx)                        // info.TPS / RealTPS / Paused / LastAutosave
_, _    = sim.Resume(ctx, prev.PreviousTimeScale) // unpause at the prior speed
_, _    = sim.Reload(ctx)                       // reload most recent save
res, _  := sim.NewSave(ctx, "default", outPath) // mint a seed save from a scenario
```

`*ipc.Session`, `*ipc.OpaqueNode`, and `*noderuntime.Runtime` all satisfy
`simctl.Requester`, so this also composes with the process launcher in
`noderuntime`.

---

## Extending with a new command

1. **Go**: add a `Command*` constant + payload/result types in `ipc/commands.go`,
   and a method on `simctl.Client`.
2. **DLL**: write a handler `object MyCmd(JToken payload)` in `SimCommands.cs`
   and `server.Register("MYCMD", MyCmd)`.
3. **Test**: extend `simctl/simctl_test.go`'s fake server to cover it.

Handlers run on the Unity main thread, so they may read/write game state
directly. Keep the JSON field names identical on both sides.
