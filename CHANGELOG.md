# Changelog

The version is in `Directory.Build.props` (`<Version>`), shown in the app's window and sent with bug reports. The last
number goes up with every build handed out with fixes or changed behaviour, the middle one with a bigger set of new
things.

## 0.7.38 — 2026-11-02

- The -full release brings the Visual C++ runtime the OCR needs: without it the scanner didn't start on some PCs. The other
  zip needs it installed, as it says now, and a scanner that can't start says why and where to get it.
- The log is written off the window's thread: a slow disk or an antivirus stood the window and the overlay still.

## 0.7.37 — 2026-11-02

- On the Consume target pick the Warden reads right at once after a bite through Scratch Marks: the old Inmate stayed for up
  to some seconds, and its target was hinted where the game no longer offered it.
- No hint on a chain's end while the Chromatic Inmate goes ("Cancel, End" showed for a second).
- The run's theme from its icon at the Sandbox's bottom left, in any language and whenever the app starts: three reads in a
  row make it the run's, and it ends with the run.
- The app no longer fails right at the start on Windows 10 with a recent CPU ("FailFast exception"): .NET's hardware stack
  protection (CET) is off.

## 0.7.36 — 2026-11-02

- A decision row read in part no longer means the other cards are used: End's place tells how many cards stand left of it,
  and their icons what they are. With the game in another language than the one taken, "End" alone read and End was hinted
  most days.
- While the game's text doesn't read in the language taken, the overlay asks to choose the game's language, in all of the
  game's languages, also after "Not now".
- The Game language list works with "Detect automatically" on: a language chosen in it turns detection off.

## 0.7.35 — 2026-11-02

- The game's language is asked for at the first connection to the game, with the one found in the game's settings marked,
  and kept in `GameLanguage`. "Detect automatically" is a setting of its own (`DetectGameLanguage`): turned off, it goes back
  to that language. A language already set in the app is not asked for again.
- The Chinese OCR recognizer ships with the program (also read for Japanese): 13 MB more in each zip, no download to wait
  for or to fail.

## 0.7.34 — 2026-11-02

- The game's UI scale comes from its log where the log tells it (`PageScale`); measuring stays for logs that don't. A UI
  drawn smaller than the window's size calls for (a 4K player's 0.64) took a pick's rough measure, and the Sandbox went
  unread until the app restarted.
- A dark bottom of the screen no longer reads as a band under an old picture of the game: the frame was cut, the Sandbox
  went unread and the overlay warned of the window's size (1124x782 windows, one reading at 1650x1087).
- Bug reports about six times smaller: a JPEG of the frame instead of two PNGs (4K frames at half size), a smaller
  screenshot, four frames of history instead of six. In the report's zip, `screen.png` is now `screen.jpg`.
- Windows 10 older than version 1903: said once in a message and the status line that the app needs 1903 or later; the
  capture is no longer retried every second.

## 0.7.33 — 2026-11-02

- Game logs of pages only (and no log): a Warden's Inmate with no target no longer reads as today's Consume used. The app
  remembers the target picks it saw; a Replace's worth now counts the Consume it may bring, where End was hinted too often.
- The game language list no longer throws when the OCR models are gone from the program's folder: it says so once and
  what's downloaded still counts.
- tools: `tartarus-sim` policy suffix `:nolog` (the old misreading, to measure it).

## 0.7.32 — 2026-11-01

- The game's settings file (its language) is read off the UI thread: opening it on a slow drive stood the window still.
- OCR language download: every host tried three times, a file cut off goes on from where it stopped, and a failed
  download is offered once more three minutes later.
- Windows.Graphics.Capture unsupported (remote desktop, virtual machine, old display driver): said once in the log and
  in the status line, no longer retried every 2 s.
- tools: `tartarus-sim --rules noose=w0,..,w4` (Replace rarity weights with Hangman's Noose held).

## 0.7.31 — 2026-11-01

- The app's window stays on top only while the game (or the window itself) is in front: over other programs it is an
  ordinary window, not in the way.
- Off the board again after a run: a last Sandbox reading left unconfirmed (the run's end fading) kept the screen
  read every tenth of a second in the lobby, picks searched and all (1.24 cores), instead of every 5 s.
- The Hunt card clicked, its target pick takes ~2.2 s to come up: the hint now stays meanwhile, the target's cell
  framed dashed (it went away for those two seconds, though the target was known).
- Only a slow scan (over 0.25 s) rests as long again before the next: on a fast PC the rest held every confirming
  reading back ~0.1 s (a new day's hint 0.27 s after its first reading with cards).
- Hints sooner: a reading not confirmed yet is searched ahead, alongside the next scan, and its hint shows the moment
  that scan confirms it (the search waited for the confirmation before, 0.15-0.47 s of every hint and more on a slow
  PC); the confirming scan comes 0.1 s after the last one, not 0.2.
- The search's value and policy networks no longer zero their scratch on every call: it took two thirds of their time
  (12% of the app's CPU in a player's trace); the same moves, the simulator 8% faster.
- A frame read as a Warden's Treasure or a pick fades in no longer changes the run's Torture Instruments or
  Objective: a held slot's instrument and the Objective change only when read so three times in a row (a report:
  Kin's Coin of Fate read as Ravenous Chains and a done Behind Cell Doors as Survival of the Fittest 0/4, and Thumb
  Ring was hinted until the eye button showed the Sandbox again, then Crimson Vintage).
- Memory no longer climbs to 1-1.6 GB: the networks' inputs, the text detector's labels, the Sandbox check's
  features and the report pictures reuse their buffers (off the board 5.7 MB a second was allocated, now 1.2; large
  objects 5.4 -> 0.6), and the game's log is read keeping only its "S16" lines (a 200 MB log's first read left
  hundreds of MB of strings to collect). The readings are the same, frame for frame.
- The Objective's pick that comes up before the landing is read (the cards cover the Sandbox at once) is searched
  with the Warden on the Destination that did it (the marked one for Undercurrents, the one far enough for Prison
  Walk), not still at the morning's cell with a move to make: the shown chests jumped by up to 1 Chromatic there.
- The neural networks share one memory arena that grows by what is asked (it doubled before), without memory
  patterns: the app's memory no longer climbs through a run (510 MB before one, 1.27 GB at its peak); on 220 frames the
  peak fell from ~535 to ~390 MB, the scans as fast.
- No End or Replace hint on a new day's first frames: the last day's decision cards still going away were read as
  the new day's (half a second on 11 of 231 new days); a day begins with its move.
- Less memory and less work for the graphics card: frame buffers are reused instead of a new one per scan (14.7 MB
  at 2560x1440, 65 MB at 7680x2112); the frames kept for a bug report are half-size JPEGs (six whole frames held
  88-390 MB); the overlay is drawn again only when what it shows changes (it was redrawn whole at every scan); and
  it is put back on top of the game only when the game climbed above it, not five times a second (each time WPF
  waited on its render thread, where the window stood in some players' hang reports).
- The Objective's pick that comes with a move onto a Prison Incident marker (always for Undercurrents, and Prison
  Walk done on a marker) is hinted with the incident still to come: the game shows the pick first and plays the
  incident out after its Confirm, and the search took the incident as gone. The shown chests fell 0.7 Chromatic on
  average at Undercurrents' pick (1.8 in a run of 2026-11-01, 0.8 now). The rules play it in the game's order too.

## 0.7.30 — 2026-11-01

- Lighter on the PC, so the game keeps its frame rate: the app runs at below-normal priority; off the board (menus,
  the map, the Hideout: neither a Sandbox nor a pick read for 3 s) the screen is read every 5 s, and the game's log
  (a click, a page) brings the usual pace back at once; a scan and its hint rest at least as long as they took (a slow
  PC read the screen back to back); text recognition uses a quarter of the cores at most (was 4 threads); the neural
  networks' worker threads sleep between steps instead of spinning (a scan took 1.1 s of CPU for 0.13 s of time, now
  0.33 s, and is faster too).
- After a Warden's Treasure that changes nothing on the Sandbox, the hint comes back after 2 s, not 4: the same Sandbox
  after its Confirm was held for the old one still on screen.
- A Prison Incident's window opening is no change of the Sandbox (its Confirm is): an instrument pick taken as one
  opened asked for the eye button.
- OCR language downloads ask huggingface.co and hf-mirror.com at once and take the first to answer: in China the
  download no longer stands still for 10 s before the mirror is tried.
- Without the game captured, the window says whether it waits for the game window or finds it minimized, not just
  "Not capturing".
- Chinese and Japanese are read by the mobile text recognizer (16.5 MB, the same alphabet) instead of the server one
  (84 MB): a Sandbox read in 3 s on a player's PC, and in 45-130 s on another's, so hints came late or never. It reads
  the report frames alike in half the time. Players with the old one are offered the new download.
- The Consume or Hunt card framed as the best move comes with its target's cell framed dashed: the next click is
  seen before the target pick is read, and on the pick that cell stays the hint while it is as good as the best
  (within 0.05 Chromatic).
- "Hide alternatives" is gone: the best move is always framed alone (the dashed frames of moves as good were off by
  default).
- A shown move is kept over a new best only while as good as it (within 0.05 Chromatic, was 0.15), and the overlay
  frames the kept move (it framed the new best while the log named the kept one).
- Destination cards still on screen 2 s after a move's click get their hint again: a click the game logged but didn't
  take left the day without one.
- No pick hint on the cards a Re-roll is taking away: one of the old three flashed after the click.

## 0.7.29 — 2026-11-01

- A Destination hint no longer goes away when its cards aren't read for a moment (a card under the mouse grows):
  only another Warden, other cards or a new Inmate in the Warden's cell take a hint away before the new one settles.
- A Re-roll counter read again as its Re-roll is clicked (the old cards fading) no longer gives the Re-roll back: the
  Objective's pick hinted a Re-roll the run no longer had.
- The Brass Horn of Strife reads in Simplified Chinese ("斗争铜号" on the card; the game's text table has another
  name for it).
- Closing the app no longer stands its window still while a big text recognizer is freed (the Chinese one took 5 s).

## 0.7.28 — 2026-11-01

- Hints come sooner after a move:
  - a new position needs two alike readings of the screen as before, but no longer a third: a cell's change was
    held back a scan for the display and then waited for another;
  - the reading that confirms it is taken 0.2 s later, not a whole scan period;
  - a click, a page or a new day in the game's log (now read every 0.15 s) starts a scan at once, and scans come
    every 0.2 s for the next 1.5 s;
  - once the log has the day's Destination clicked, the cards still going away are no Destination hint (two quick
    readings alike took a card left for the only Destination).
- The hint shown goes as soon as the screen shows another position (the Warden moved, another phase, other
  Destinations, a new Inmate in the Warden's cell), not when the new one settles: no "Cell 16" over the Warden
  walking into it.
- After a Replace, the game's log tells when its new Inmate showed: one of the same Code and rarity as the old no
  longer holds the hint back for 2 s.
- The app's log has milliseconds.
- The cell network reads the Prey mark (the Blood-Stained Trap's Hunt): retrained on 164 marked cells found by the
  game's own icon and checked by hand (20 before); it missed most of them, or took them for a Tusk.
- A Prey mark (the Blood-Stained Trap's Hunt) read as a Tusk is taken for what it is: a mark is the one the
  instruments held can make, on an Inmate it can be on (a Prey mark on a Wolf or Rat, a Tusk with Iron Tongs of Lust).
- The Consume target pick (the red screen) keeps the Tusk and Prey marks read just before it: the red hid a Prey
  mark, and the Consume hinted a moment before was ranked under Cancel there.
- The app's log names the best move on the Consume target pick as the overlay does (Cancel among the targets).
- Text recognition for another language downloads from hf-mirror.com when Hugging Face sends nothing for 10 s (it is
  blocked in China: a TapTap player's Chinese recognizer never came); the window says which host it comes from.
- A graphics card reset no longer stands the app still: the overlay stays up while the game's window is captured
  again (hiding it then waited 10 s on Windows).
- The game language is asked for only from plain Sandbox frames: a Consume's red screen and fades darken the
  Objective's panel, and a chain of Consumes asked for it with English read right.

## 0.7.27 — 2026-10-31

- Game language in the window (Recognition): taken from the game's settings file by default ("Detect automatically"),
  or chosen in the list by hand when the game shows another one (a TapTap client in Chinese wrote "en" there, and its
  Remaining Days and Destination cards weren't read). The app itself stays in English.
- Simplified Chinese is read as such: the game writes its code "zh", which was taken for English.
- When the game's text doesn't read in the language taken (the Objective's name not found on Sandbox after Sandbox), a
  window asks which language the game shows; the choice goes to Game language, auto detection off.
- A hang report reads the stacks with the runtime's own DAC, never downloading one (a player's PC refused the
  download: the report came without a stack), and tries a refused process snapshot again.

## 0.7.26 — 2026-10-31

- The Torture Instrument pick an Objective brings is hinted with the Objective done: the panel under the cards could
  still read it unfinished, and the hint played on as if a second pick were coming (a Re-roll worth more than any card
  was passed over).
- A Prison Incident's effect is told from its window's Confirm, not its opening: a pick that comes right after one asks
  for a look at the Sandbox (the eye button) instead of hinting over the cells as they were before it.

## 0.7.25 — 2026-10-31

- The solver's rules no longer take away a Destination on some mornings for no reason: the game shows two only with
  Barbed Whip, Forged Key or Burning Stake (checked against ~1100 logged mornings). The expected chests shown are
  recalibrated to it and the hint quality levels measured again.
- The app's window no longer stands still for seconds at a time while it looks for the game's window on a busy PC.
- A graphics card reset (a driver update, waking from sleep, a GPU timeout) no longer stops auto scan for good: the
  game's window is captured again.

## 0.7.24 — 2026-10-31

- A pick the game clicks for the player ("Quick Decision") now counts as taken: the Sandbox no longer asks to go
  back to it for the rest of the run (the eye button framed instead of the Destination hinted); and a new day closes
  any pick still counted open.
- The eye button no longer flashes as a Warden's Treasure comes up or is taken: the game's log tells the pick a moment
  after the screen, so the eye button's own clicks now say when the Sandbox is a look from a pick.
- The bug report window closed while its report was being sent no longer ends in an error.

## 0.7.23 — 2026-10-30

- Opulence: the Chromatic count no longer goes up with each of a Warden's Treasure's three picks, nor with a pick the
  game shows again when the server is slow to answer (4 Chromatics were taken for a full panel, and the hints played
  as if it were).

## 0.7.22 — 2026-10-30

- The eye button's ring no longer stays on the screen after a run: it rang on in the Hideout when the run ended
  right after a Warden's Treasure.
- Windows 10: frames are cut to the game's picture again; Windows 10 captures the whole window rectangle, title bar
  and borders included, and nothing was read in a window the game didn't fill.
- A game window resized by its edges may keep the picture it drew before, too big for it and cut off on the right:
  the overlay now says so (set the window size in the game's settings, or press Alt+Enter twice), and a 16:9 picture
  left at the top left is read as such.
- Tusk marks: the cell network retrained with about 170 more hand-checked ones, on Wolves, Lions and Rats too (the
  mark is the cell's and stays under whoever comes in after the Mammoth); before, it found about half of them and
  flickered on and off on the same cell.

## 0.7.21 — 2026-10-30

- Warden's Treasure on game clients whose log has only page names: a look at the Sandbox with the eye button no
  longer counts as a new Chromatic and a change of the Sandbox, so the pick stops asking for the eye button again and
  again and the Chromatic count stays right; a look away from an instrument pick is no new pick either.
- Re-roll is no longer hinted after the last one was used on such clients (the counter goes away with it; other cards
  on the same pick now count as a Re-roll made).
- Windows 10 1903 / 1909: capturing works (it failed on a setting those versions don't have).
- The capture starts and stops off the window's thread: on a Windows Insider build it stood still at every start.
- A pick card clicked (it grows and its name shrinks) and read for a moment as nothing known no longer turns the hint
  to the best of the other cards: the hint for the whole pick stays.

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
