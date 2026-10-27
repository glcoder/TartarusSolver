# Changelog

The version is in `Directory.Build.props` (`<Version>`), shown in the app's window and sent with bug reports. The last
number goes up with every build handed out with fixes or changed behaviour, the middle one with a bigger set of new
things.

## 0.7.7 — 2026-10-27

- Re-rolls left when a pick's counter doesn't read: the last count read in this run less the Re-rolls clicked since,
  else none. 0.7.5 took one per run for granted and hinted a Re-roll where there was no button (the run's Re-roll
  comes from a Pactspirit, Twin Inquisitors, and only by chance).
- Under the Consume target pick's red screen no Inmate reads lower than just before it (a rarity never drops in its
  cell): an Ultimate's red frame was lost in the red, a Mammoth Ultimate read Normal and was hinted as a meal the game
  doesn't allow.

## 0.7.6 — 2026-10-27

- The Re-roll button and its "Remaining attempts" are held to the screen's bottom, as the game places them: on a 16:9
  screen (2560x1440 full screen) the Re-roll hint was framed a button's height below the button.

## 0.7.5 — 2026-10-27

- A Torture Instrument pick's first frames, before its Re-roll counter shows, take the Re-rolls left from the game's
  log instead of none: no more "take the instrument" hint for a moment where a Re-roll is better. A Warden's Treasure
  pick weighs the Re-rolls the run has left too (it took one for granted).

## 0.7.4 — 2026-10-27

- The player's window has the hint quality slider too, and auto scan is on in it at every start.

## 0.7.3 — 2026-10-27

- The window is the player's by default: the game window, auto scan and its period, the game's log, the version and
  bug reports; the overlay shows the hints alone and nothing is saved to the dataset (the app's log is written, for
  bug reports). `--debug` on the command line brings the whole window back (overlay views, hint quality, labels,
  dataset, the log); its choices aren't touched by the player's window.
- Auto scan's period on a slider, 0 to 1 s by 0.1 (0.5 by default, on by default): from one frame's start to the
  next, never before the last frame's reading and hint are done; at 0 the next frame comes as soon as they are.

## 0.7.2 — 2026-10-27

- The run's first Torture Instrument pick is weighed over the first Inmates it may bring out (they come out after
  it), not over one drawn start, and with the Objective the Sandbox showed before the cards (it was one drawn
  Objective, the same for every run); unread, every Objective is weighed.
- A pick over a Sandbox whose day counter didn't read (a popup darkens it) takes the day from the game's log: an
  Objective's pick on day 2 was weighed as day 1.
- The first Sandbox frame after another screen, caught mid-fade (no Warden, odd cells), no longer holds the next good
  frame's cells back: half a second of hints on a mixed Sandbox (an "End" worth 3.9 Chromatic instead of 7.7).

## 0.7.1 — 2026-10-26

- Make Your Decision with only End, left to click when a merge took the Warden's Inmate away (the game ends such a
  day by itself otherwise), gets its hint: the End card framed with the chests the run is expected to end with.
- No "End" hint for a moment after Replace: the card leaves before the new Inmate shows, and the old one was taken for
  the day after the Replace; the hint waits for the new Inmate (two seconds at most).
- A pick that comes up right after a change (an Objective done as a door opens on its Inmate) is weighed on the
  Sandbox as last read, not as it was a scan before (the Wolf Ultimate that did it was missing from the hint).
- Cells coming out from under a cover (an animation, a popup) take their reading at once, and the pulsing Consumable
  mark no longer holds a reading back: two Lions read as Unopened for a hint after a Warden's Treasure pick.

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
