# Changelog

The version is in `Directory.Build.props` (`<Version>`), shown in the app's window and sent with bug reports. The last
number goes up with every build handed out with fixes or changed behaviour, the middle one with a bigger set of new
things.

## 0.7.20 — 2026-10-30

- Cell reading: the network retrained with about 200 more hand-checked cells; an Unopened cell's Legendary glow is
  seen again (0.7.19's network took such cells for plain Unopened ones on a bright Sandbox), a red glow on one is
  Ultimate, not Normal, and an Ultimate Lion is no longer taken for a Consumable one so often.
- Statistics: a run the game was restarted in (a new log file) is no longer counted again when the app starts next.

## 0.7.19 — 2026-10-29

- After a change of the game's resolution (windowed to fullscreen, say) the game may keep drawing its UI at the old
  one's scale until Alt+Enter; the hints were then drawn too big or too small and off their cards and cells. The
  scale is now measured: on a pick by where its cards' names stand, on the Sandbox by the scale it is found at; when
  the game catches up, every few seconds a screen not read is tried at the window size's own scale again.
- Cell reading: the network retrained on 840 hand-checked cells where two networks read differently; dim Inmates
  (Mammoth Ultimate, Lion Legendary / Ultimate and others) are no longer taken for Normal ones.
- Update: the window keeps answering while Windows checks the new version before it starts (it stood still for
  seconds and sent a false hang report).

## 0.7.18 — 2026-10-28

- Steadier hints: a new position on the Sandbox is hinted once two scans in a row read it alike, so a frame in the
  middle of an animation (a Chromatic's flight, a popup fading in) no longer moves the frame there and back; the hint
  shown stays meanwhile.
- No hint for the Sandbox the game has already changed: after the log tells of a bite, a Chromatic, a Prison Incident
  or a Warden's Treasure taken, a reading like the one just before it is the old Sandbox still animating (up to 4 s).
  In chains the eaten Inmate was framed as the next bite for up to 5 s (25 times in 4 runs).
- Two Warden's Treasures in a row (two Chromatics at once): the second is no longer hinted over the Sandbox from
  before the first was taken (a Three-Headed Thumb Ring that had opened three cells was hinted again). A pick plays
  on the latest reading of the Sandbox after the first was taken, or asks for the eye button when there is none; a
  frame caught as a popup fades in or out is no such reading (a Treasure was hinted over one at 9.9 Chromatic
  instead of 10). A look through the eye button always counts, so the eye button is no longer asked for again and
  again, and "back to the pick" is framed only once the Sandbox viewed from the pick has been read.
- The Hunt's target pick is hinted as one: its banner reads "Select an Objective" like the Sewer Ratway's, and the
  hint framed a cell to go to. After the Hunt card on the same day and cell, the best Hunt target is framed on its
  cell, from the Sandbox read with the cards (the pick dims the other cells, an Ultimate read Normal there). The
  banner still showing as the next day begins is no decision, and a Warden's Treasure or instrument pick never
  carries a Sewer Ratway or Interrogation Room choice (a Treasure after a Hunt showed no hint at all).
- The overlay moves onto another monitor small and takes the game's size there: on a 4K screen with another DPI than
  where it started, the window's first placement hung for seconds while WPF scaled the screen-sized overlay.

## 0.7.17 — 2026-10-27

- Fewer false hang reports: the window's start may take up to 30 s (a cold start loading WPF's theme took 5 s), and
  a UI thread that answered or stood idle by the time its stack was read (a PC back from sleep) is only written to
  the app's log, not sent.

## 0.7.16 — 2026-10-27

- The Consume target pick no longer shows an Inmate at a rarity it had before it left: a cell whose Inmate went
  (became Chromatic, was consumed) and was taken by a lower one of the same Code read the old rarity on the red
  screen, and the hint there could turn to Cancel right after Consume was hinted on the decision.

## 0.7.15 — 2026-10-27

- "Hide alternatives" in the window's Hints (on by default): the best move is framed alone. Off, the moves as good as
  it (within 0.05 Chromatic) are framed beside it, dashed now, all of them (0.7.14 framed the best alone when two or
  more were; that rule is gone).
- The app's log says when the update check found nothing newer ("Update check: 0.7.15 is the latest version"), once
  a start: a check that ran is told from one that didn't.

## 0.7.14 — 2026-10-27

- More decisive hints: a move as good as the best one (within 0.05 Chromatic) is still framed thin beside it, but
  when two or more are, the best is framed alone: all of them are about equal and any will do (players found the
  many frames confusing).
- The hint's long line at the top of the overlay is the debug window's only; the player sees the frames and the
  chests over them.
- Statistics in the window: the runs played, the Chromatic Inmates they ended with in all and per run; Reset clears
  them (after asking). A run counts once the game's log has seen it from its Start to the Riot's result, counted
  from this version's first start on; kept in `%LOCALAPPDATA%\TartarusSolver\statistic.json` (with each run's time,
  not shown yet).

## 0.7.13 — 2026-10-27

- Updates: the app asks GitHub for a new version at start and every 6 hours ("Check for updates", on unless taken
  off; the `--debug` window never asks). A new one shows an "Update to …" button: it downloads the same kind of zip
  (with .NET or without), checks it against the SHA-256 GitHub gives, and the new version puts itself in place of
  this one and starts. Settings and downloaded OCR languages stay. A build from source gets the release's page.

## 0.7.12 — 2026-10-27

- The window no longer freezes at the first scan (a player's dump, an AMD card): Windows hands the app a new frame
  holding the graphics device's lock, and the frame's copy for a scan, holding the app's own lock, waited on the
  device's, while the new frame waited on the app's. Each waited on the other for good; a frame that comes during a
  copy is now let go instead (the next one is kept). In 0.7.11 the window answered, but scans stopped all the same.

## 0.7.11 — 2026-10-27

- A window that stops answering reports itself: when the app's own window doesn't answer for 5 seconds, where it
  stands (its threads' stacks, read from a snapshot of the app itself) goes to the app's log and to the developer
  with the log, without asking (a frozen window can't). Players saw the window freeze as it caught the game's.
- A frame is copied from the graphics card off the window's thread: a slow or stuck copy no longer freezes the window
  (the likeliest cause of the freeze above), and stopping the capture doesn't wait for it.

## 0.7.10 — 2026-10-27

- The chests a hint shows are what runs end with: the search's expectations ran low (its playouts play worse than the
  player it advises), by 0.8 Chromatic at a run's start down to none at its end (3000 runs; the player's real runs:
  +0.5 on average). A table by Remaining Days, `models/solver/chests.txt` (`tartarus-sim calib`), corrects them; on
  the real runs the shown Chromatic now miss by +0.1 on average instead of +0.5. The moves rank as before.
- The playouts' moves no longer see behind the doors: they looked one step ahead on their world, where every door's
  Inmate and every marker's incident is drawn already, and picked the Destination with the best Inmate behind it.
  Now they look on a world of their own drawing. As strong as before (4000 paired runs: +0.03 ± 0.04). The hint
  quality table is remeasured with them (4000 runs: 32…2048 playouts = 6.78…7.69 Chromatic, the same within noise;
  a hint takes 5-10% longer).

## 0.7.9 — 2026-10-27

- The game's log of some clients names only the pages they open (no sounds, clicks or day counter): it is read too,
  for the run's start and end, its days, Chromatics, Prison Incidents and picks. Such a log told no run before, and a
  log that tells none no longer stops a new run from being told by Remaining Days going up: the last run's 10
  Chromatics went on into the next, and every hint was for chests alone (a player's bug report).
- Remaining Days that don't read (the red Consume screen, a popup) are the last read in this run when the log can't
  tell them, not the run's first day.

## 0.7.8 — 2026-10-27

- The simulator gives a run its Re-roll at 50%, as the Pactspirit Twin Inquisitors does (`--reroll-percent` changes
  it); a position the screen shows keeps the Re-rolls it reads. The hint quality table is remeasured under it (4000
  runs: 32…2048 playouts = 6.76…7.68 Chromatic; fewer than before because half the runs have no Re-roll).
- Optional `models/solver/picks.bin`: a network of its own for the playouts' Torture Instrument picks (`policynet.py
  --instruments`), used when the file is there. Not shipped: on 4000 paired runs it ties the heuristic picks.
- `tartarus-sim`: `objcheck` (the search's values around a done Objective's reward pick), `policydata --values-out`
  (the same runs' mornings for the value network, with the search's value), `--instrument-net`, `--policy-instruments`.

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
