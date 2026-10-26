# Changelog

The version is in `Directory.Build.props` (`<Version>`), shown in the app's window and sent with bug reports. The last
number goes up with every build handed out with fixes or changed behaviour, the middle one with a bigger set of new
things.

## 0.7.0 — 2026-10-26

- The search's playouts make their moves by a policy network (`models/solver/policy.bin`) trained on the search's
  own choices, instead of the static evaluation: at the app's 1024 playouts per move the hints end runs with
  +0.26 Chromatic against the old search at 2048 (the same time), 8.02 Chromatic per run on 4000 paired runs.
- The hint quality levels remeasured with it (4000 runs): 7.51 Chromatic per run at 64 playouts per move, 7.98 at
  1024 (were 7.32 and 7.79).
- The value network's first layer adds up only the features that are set: the search is about 17% quicker.
- The control window is wider: every caption fits.
- `tartarus-sim policydata`, `tools/train/policynet.py`: the policy network's training data and training.

## 0.6.1 — 2026-10-26

- The hint quality levels (playouts per move: time and Chromatic per run) remeasured on 4000 runs under the
  chest-worth weighing; the old ones came from 400 runs and the earlier rules.
- The app looks for its models only beside itself (and, run from the source tree, in the repository up to its root),
  never in the folders above.

## 0.6.0 — 2026-10-26

Hints
- The hint is the chests the run is expected to end with: the game's Chromatic and Ultimate chest icons over the
  frame, "×8.0" by each (tenths, rounded); the best move framed bold, moves as good thin.
- Moves are weighed by the Riot's caches too (settings `CacheWorth`: a cache per Inmate left, by rarity, in
  Chromatics), not by Chromatics alone: a sure Ultimate isn't thrown away for a 5% chance at a Chromatic.
- On a pick whose Sandbox changed underneath (a chain, Chromatics leaving), the eye button is ringed: look at the
  Sandbox, then back to the pick (ringed again) for a hint on what it is now.
- The value network's guesses kept to what can be (no fewer Chromatics than contained, none past 10).

Rules
- A Replace only before the day's Consume (the game's log: never after one).
- A chain going on is told from the log and the screen (Consume card or red screen after a bite): eat on or stop,
  no Replace.
- The run theme now reaches every hint (it was lost in a copy: Cage Fight's wider diet, Iron Law's 30 days, ...),
  and is remembered across restarts of the app for the run it was read in.
- Survival of the Fittest and Undercurrents progress counted from the log over the red screen.

Reading the screen
- Any game language: decision cards, Destinations and pick screens told by their pictures; OCR for English and
  Russian (East Slavic), other languages' recognizers downloaded on asking (with a progress window); the game's own
  texts in all its languages (`models/text/tartarus_text.json`) to match what's read.
- Chromatics counted from their own sounds and panel slots (one flight sound may serve two at once), and from the
  Warden's Treasures they bring.
- Half-hidden Sandboxes (a Chromatic's flight, a popup) no longer taken for the position.
- The cell network retrained: Rat Nest, Tusk and Prey (Prey from 8 real cells and semi-synthetic ones).
- The Objective's popup no longer taken for a Warden's Treasure pick.

App
- Bug reports to Sentry (Ctrl+Shift+B): screenshot with the overlay, recent frames, the app's log, this run's game
  log, settings; crashes too.
- English interface; hotkeys Ctrl+Shift+…, active only while the game or the app is in front; auto scan every 0.5 s.
- "Hints only" overlay mode; "Interactable regions" frames.
- `tools/package.py`: the zip for testers (`--full` with .NET inside).
