# fo4-mcp

Drive a running **Fallout 4** from outside the game: run console commands, read the Papyrus log, press keys and
take screenshots, from a command line or from an AI agent over **MCP** (Model Context Protocol).

It has two parts:

- **F4Exec** (`plugin/`): an F4SE plugin that listens on a named pipe, runs the console lines it receives **on
  the game's main thread**, and sends back the console output they produced.
- **fo4-cli** (`cli/`): a .NET command-line client for that pipe, which also works as a stdio **MCP server**.

Built while making a companion mod, to replace the "alt-tab, type in the console, read the result" loop with
something a script or an agent can do.

## Requirements

- Fallout 4 **runtime 1.11.240** (the plugin checks the runtime and refuses to load on any other).
- [F4SE](https://f4se.silverlock.org/) **0.7.9**, with the game launched through `f4se_loader.exe`.
- [.NET 10 runtime](https://dotnet.microsoft.com/download) for `fo4-cli` (Windows).

The plugin calls game functions by address, so a game update needs new addresses (see "How it works").

## Install

1. Copy `F4Exec.dll` into `Fallout 4\Data\F4SE\Plugins\`.
2. Put the `fo4-cli` folder anywhere, for example `C:\Tools\fo4-cli\`.
3. Start the game with `f4se_loader.exe`, load a save, then:

```
fo4-cli status
fo4-cli exec player.getpos z
```

The plugin writes `Documents\My Games\Fallout4\F4SE\F4Exec.log`.

## CLI

```
fo4-cli exec <command...> [--json]     run one console line, e.g. fo4-cli exec player.getav Health
fo4-cli run <file> [--json]            run each line of a file (like bat; ';' lines are comments)
fo4-cli logs [-f] [-n N] [--grep RE]   show Papyrus.0.log (-f follows it live)
fo4-cli logs --enable                  turn on Papyrus logging in Fallout4Custom.ini
fo4-cli status [--json]                game / plugin / logging status
fo4-cli key <enter|esc|tilde|...>      press keys in the game window (e.g. dismiss a popup)
fo4-cli screenshot [-o FILE] [--width N] [--nohud] [--jpg]
                                       capture the game window (borderless/windowed)
fo4-cli mcp                            run as an MCP server on stdio
```

A selected reference persists between calls, as in the real console:

```
fo4-cli exec "prid 0001A4D7"
fo4-cli exec getav Health
fo4-cli exec 'cf "Actor.EvaluatePackage"'
```

## MCP

Add it to an MCP client as a stdio server that runs `fo4-cli.exe mcp`. For Claude Code:

```
claude mcp add fo4 -- C:\Tools\fo4-cli\fo4-cli.exe mcp
```

Tools:

| Tool | What it does |
|---|---|
| `fo4_exec` | run console commands in order, returns each command's output |
| `fo4_logs` | read the Papyrus log (tail, or incrementally with a cursor), optional regex filter |
| `fo4_status` | is the game running, is the pipe reachable, is Papyrus logging on |
| `fo4_screenshot` | capture the game window and return the image (optionally with the HUD hidden) |

With screenshots an agent can check its own work: place an object with the console, frame it with `setangle` or
`tfc`, look at the result.

## Notes and limits

- **World-changing commands** (`load`, `coc`, `cow`, `moveto`, ...) return no output: they run a lot of game logic
  inside the call, so capture is switched off for them.
- During **loading screens** the main thread does not process requests, so calls time out. Poll with a cheap
  command (for example `player.getpos z`) until it answers.
- `fo4-cli key` sends real scan codes (Fallout 4 reads input through DirectInput, which ignores window messages),
  and brings the game window to the front first.
- **Screenshots** copy the window from the screen, so the game must be in borderless or windowed mode and not
  covered. Exclusive fullscreen captures black; the command detects that and says so.
- There is no walking or mouse-look: move the player with `moveto` / `setpos` and turn with `setangle`.

## How it works

The plugin hooks the console's line handler and the per-frame function that runs queued console scripts. A request
arriving on `\\.\pipe\fo4_console_exec_v1` is handed to the main thread, which runs each line as if it had been
typed (the console's "is the console menu open" check is satisfied for the duration) and collects what the
console printed. Before patching anything it compares each target's first bytes with the expected prologue, so on
an unknown runtime it logs an error and stays inactive instead of crashing.

Protocol (message-mode pipe, one JSON reply per request):

```
ping                       -> {"ok":true,"plugin":"F4Exec","version":1,"pid":1234}
exec\n<line>[\n<line>...]  -> {"ok":true,"results":[{"command":"...","output":"..."}]}
                              {"ok":false,"error":"..."}
```

## Building

```
git clone --recursive https://github.com/crispycranky/fo4-mcp
cd fo4-mcp

# plugin (Visual Studio 2022, CMake 3.18+); only the F4SE / common headers are used
cmake -S plugin -B plugin/build -A x64
cmake --build plugin/build --config Release      # -> plugin/build/Release/F4Exec.dll

# CLI (.NET 10 SDK)
dotnet build cli -c Release                      # -> cli/bin/Release/net10.0-windows/
```

## License

MIT, see [LICENSE](LICENSE). The submodules in `extern/` (F4SE and common, by Ian Patterson et al.) keep their own
licences.
