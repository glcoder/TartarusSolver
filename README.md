# Tartarus Solver

Move hints for **Tartarus Prison**, the Sandbox board mode of *Torchlight: Infinite* (season 16), drawn as an overlay
over the game's window.

The app reads the screen and the game's log file, works out the position, plays it out thousands of times with the
game's rules, and frames the move that ends the run with the most Chromatic Inmates (and, after them, the richest Riot
chests).

> **What it does not do:** it never reads or writes the game's memory, never injects anything into the game, and never
> presses keys or clicks in it. It only captures the game's window (the Windows screen-capture API) and reads the log
> file the game writes to disk. Every move is yours.

## Features

- **Move hints:** the best move framed bold green (with "Hide alternatives" off, the moves as good framed dashed
  beside it), with the chests the run is expected to end with (Chromatic and Ultimate) over the frame. Hints for every
  decision: Destinations, Make Your Decision (Consume, Replace, Hunt, End, Instigate, Bestow), the Consume target pick
  and chains, the Hunt's target pick, the Sewer Ratway, the Torture Instrument picks (with Re-roll) and the Warden's
  Treasure.
- **The game's rules:** all 31 Torture Instruments, the 17 Prison Incidents, the Objectives, the run themes (Tyranny,
  Opulence, Iron Law, Cage Fight), Rat Nests, Tusk and Prey marks, Scratch Marks.
- **The solver:** a determinized Monte Carlo search (what's hidden behind doors is drawn again for every playout:
  no peeking): the playouts' moves by a small policy network trained on the search's own choices, a small value
  network at their horizon.
- **Reading the screen:** a convolutional network for the cells (Code, rarity, marks), small networks for the
  Torture Instrument and Chromatic slots, card and pick screens told by their pictures, and OCR (PaddleOCR, ONNX)
  for the text. Works in any of the game's languages: English and Russian OCR ship, others are downloaded on asking.
- **The game's log:** run start, days, Consumes, Chromatics, Prison Incidents and picks, read from the log file the
  game writes, to fill in what the screen hides (a red Consume screen, a popup).
- **Statistics:** the runs played and the Chromatic Inmates they ended with (in all and per run), from the game's
  log, kept across restarts until reset.
- **Bug reports:** one hotkey sends a screenshot with the overlay, the recent frames, the app's log and the current
  run's game-log lines to the developer (when built with a Sentry DSN; otherwise the report is saved locally). A
  window frozen for 5 seconds reports itself, with where the app stands (its own threads' stacks).

## Requirements

- Windows 10 (2004) or 11, x64.
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (not needed with the `-full` release).
- *Torchlight: Infinite* in windowed or borderless mode: an overlay can't draw over exclusive fullscreen.

## Install and use

1. Download a release zip, unpack it anywhere, run `TartarusSolver.exe`.
2. Start the game and open Tartarus Prison. The app finds the game's window and its log file by itself (or pick
   them in the control window).
3. Play. The overlay shows the hint for whatever is on screen.

New versions: with "Check for updates" on (it is by default) the app asks GitHub for the latest release at start and
every 6 hours; when there is one, "Update to …" downloads it, checks it and restarts into it. Nothing else is sent.

| Hotkey | |
|---|---|
| Ctrl+Shift+S | scan now |
| Ctrl+Shift+R | auto scan on/off (every 0.5 s by default) |
| Ctrl+Shift+H | overlay on/off |
| Ctrl+Shift+B | report a bug |
| Ctrl+Shift+E / Ctrl+Shift+D | label editing / save the frame (for building the training set; with `--debug`) |

Hotkeys work while the game or the app is in front. The control window holds the game window, auto scan and its
period (0 to 1 s; 0: the next frame as soon as the last one's reading and hint are done), the hint quality, the
game's log and bug reports. `TartarusSolver.exe --debug` opens the whole window: overlay views (regions, OCR text,
everything read), labels, dataset collecting and the app's log.

Settings live in `%LOCALAPPDATA%\TartarusSolver\settings.json`, among them:

- `CacheWorth`: what a Riot chest per Inmate left is worth, in Chromatics, by rarity (Normal … Ultimate); the
  solver weighs a sure chest against a chance at a Chromatic with it.
- `GameLanguage`: the game's language for OCR (`en`, `ru`, …); by default read from the game's own settings.
- `HintPlayouts`, `HintSeconds`: how hard the solver thinks per move (also the slider in the control window).
- `CheckUpdates`: ask GitHub for new versions (the checkbox in the window).
- `Hotkeys`, `GameLogPath`, `ReportDsn`.

## Building

```bash
dotnet build TartarusSolver.sln -c Release -p:Platform=x64
```

The OCR models (PaddleOCR, Apache-2.0) are downloaded into `models/ocr` by the first build and checked by SHA-256.
The app is `src/Tartarus.App/bin/x64/Release/net9.0-windows10.0.22621.0/win-x64/TartarusSolver.exe`.

```bash
dotnet test tests/Tartarus.Rules.Tests -c Release
```

A release zip for others (only the files the app needs, with a short how-to; `--full` bundles .NET):

```bash
python tools/package.py
```

Bug reports go to Sentry when the build is given a DSN: set `TARTARUS_SENTRY_DSN` (or pass `-p:SentryDsn=…`, or put
`ReportDsn` in the settings). Without one, reports are saved to `%LOCALAPPDATA%\TartarusSolver\reports`.

## Project layout

| | |
|---|---|
| `src/Tartarus.Rules` | the game's rules: state, legal moves, every instrument and incident, scoring |
| `src/Tartarus.Solver` | the Monte Carlo search, the heuristic policy, the value and policy networks |
| `src/Tartarus.Vision` | screen layout, OCR, the cell and slot networks, card and pick pictures, the game's log, the dataset |
| `src/Tartarus.Capture` | window capture (Windows.Graphics.Capture) |
| `src/Tartarus.App` | the WPF overlay and control window, hints, bug reports |
| `tests/Tartarus.Rules.Tests` | rules, solver and screen-text tests |
| `tools/Tartarus.ScanCli` | `tartarus-scan`: scan frames, export training crops, label review pages, log analysis |
| `tools/Tartarus.SimCli` | `tartarus-sim`: simulate runs, measure strength, self-play data for the networks |
| `tools/train` | PyTorch training of the networks (`requirements.txt`) |
| `models` | the trained networks and the pictures and texts the app reads |

## Training

The networks are trained on frames captured while playing: the app saves them (auto-collect, or Ctrl+Shift+D) into
`dataset/`, with labels checked by hand in edit mode or on `tartarus-scan review` pages.

```bash
tartarus-scan export dataset                      # crops and labels into dataset/train
python tools/train/cellnet.py dataset/train       # cells and the Warden's arrow -> models/cells
python tools/train/slotnet.py dataset/train       # Torture Instrument and Chromatic slots -> models/slots
tartarus-sim selfplay --runs 20000 --out values-1.bin
python tools/train/valuenet.py values-*.bin       # the value network -> models/solver/value.bin
tartarus-sim policydata --runs 20000 --policy vn:64:3 --out actions-1.bin
python tools/train/policynet.py actions-*.bin     # the playouts' policy network -> models/solver/policy.bin
```

Optional: `policydata --values-out values.bin` writes the same runs' mornings for the value network too;
`policynet.py --instruments --out models/solver/picks.bin` trains a network for the playouts' instrument picks, used
when the file is there. `tartarus-sim compare` judges a change (4000 paired runs), `tartarus-sim levels` remeasures
`models/solver/levels.txt`, `tartarus-sim calib --table models/solver/chests.txt` refits how a hint's expected chests
turn into what runs end with, `tartarus-sim objcheck` compares the search's values before a done Objective and on its
pick. The simulator gives a run its Re-roll at 50% (the Pactspirit Twin Inquisitors); `--reroll-percent` changes it.

## Disclaimer

A fan-made tool, not affiliated with or endorsed by the makers or publishers of *Torchlight: Infinite*. The game's
names, texts and pictures in `models/` (icons, card templates, `text/tartarus_text.json`) belong to their owners and
are included only so the app can recognize the game's screens.

## Acknowledgements

- **[CoffeeBns](https://www.twitch.tv/coffeebns)** — for
  [Tartarus Simulator](https://blahdot3h.github.io/tartarus-simulator/), the practice simulator this project grew out
  of (its rules, live statistics and screenshot reader were the starting point for the rules engine and the first
  scanner), and for the push to build it.
- **[tli-warden](https://github.com/arcasti/tli-warden)** — its data from the game client (rarity weights, the
  Torture Instruments, Prison Incidents, Objectives and themes) helped pin down the rules' numbers.
- **[TLI Overlay Plus](https://github.com/Giboork/tli-overlay-plus)** — its advisor's rule numbers were a second
  opinion to check ours against (one of them, the mornings with one Destination card fewer, is ours too).

No code from these projects is used here.

## License

[MIT](LICENSE) © 2026 Artem Gurevich. Third-party components keep their licenses: ONNX Runtime (MIT), PaddleOCR
models (Apache-2.0), Sentry .NET SDK (MIT), ClrMD (MIT), Vortice.Windows (MIT), StbImageSharp (public domain).
