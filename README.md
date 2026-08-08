# Quicksell

Dalamud plugin that reprices your retainers' market board listings.

Built on the [goatcorp SamplePlugin][template] template.

## Why

The manual loop is: bell → retainer → item → adjust price → compare prices → note the cheapest →
subtract one → paste → confirm → next. Penny Pincher removes the mental arithmetic; everything
else is still hand work, and it does not scale past a couple of retainers.

Quicksell automates the loop, and fixes three things the manual routine gets wrong:

- **It raises prices too.** If the cheapest competitor sits well above you, staying put gives
  money away. Repricing is not the same as undercutting.
- **It prices from sale history** when nobody else is listing the item, instead of guessing.
- **It ignores aggressive undercuts** — but only when the offending stack is small. Somebody
  dumping 99 units at a tenth of the going rate really will absorb the demand, so routing
  around them would be wishful thinking.
- **It gets HQ and NQ the right way round.** An HQ listing competes with HQ only, since sitting
  above the NQ prices is the whole point. An NQ listing has to beat every offer on the board
  including HQ: facing the same price, a buyer takes the HQ.

## Status

Steps 0 to 2 of the plan are done. Step 3 onwards (the automation loop) is waiting on the
in-game observations described below.

| Step | State |
|---|---|
| 0. Skeleton, config, projects | done |
| 1. Data collection + observation window | done, needs an in-game run |
| 2. `PricingEngine` + unit tests | done, 24 tests green |
| 3a. Read-only walk over all retainers | done, needs an in-game run |
| 3b. Prefetch + decisions across the walk | partial: works per retainer in the debug window |
| 4. Writing prices, one retainer at a time | done, needs an in-game run |
| 5. Returning sub-floor items to inventory | not started |
| 6. End-of-run report, stop button, abort handling | not started |

Writing prices is scoped to the one retainer whose listings are open, and dry run is on by
default. Two safety checks run immediately before every write: the open price window must be for
the expected item, and it must still be asking the price the decision was based on. Either
mismatch skips the item rather than writing.

## Layout

- **`Quicksell.Pricing/`** — the decision engine. Plain `net10.0`, no Dalamud dependency, so it
  can be tested without the game running. `PricingEngine.Decide` is a pure function.
- **`Quicksell/`** — the plugin: market data collection, request scheduling, windows.
- **`Quicksell.Pricing.Tests/`** — xUnit coverage of every branch of the decision tree.

## Building

Requires the .NET 10 SDK and a Dalamud dev install (XIVLauncher must have run the game at least
once). Set `DALAMUD_HOME` if XIVLauncher lives somewhere non-default. ECommons is a submodule,
so clone with `--recurse-submodules` or run `git submodule update --init --recursive`.

```
dotnet build Quicksell.sln -c Release
dotnet test Quicksell.Pricing.Tests/Quicksell.Pricing.Tests.csproj
```

The packaged plugin lands in `Quicksell/bin/x64/Release/Quicksell/`.

## Running in game

1. `/xlsettings` → Experimental → add the full path to `Quicksell.dll` under Dev Plugin
   Locations, or drop the built folder into `%AppData%\XIVLauncher\devPlugins\Quicksell`.
2. `/xlplugins` → Dev Tools → Installed Dev Plugins → enable Quicksell.
3. `/quicksell` opens settings, `/quicksell debug` opens the market inspector. `/qs` is an alias
   for both.

## What the in-game observations settled

Open `/quicksell debug`, then `/xllog` alongside it.

- **Market data needs no window — confirmed.** `Request (no window)` returns both offerings and
  sale history. This was the load-bearing assumption: the run can prefetch every price up front
  and only open the UI for items that actually need changing, which makes skipped items free
  rather than merely cheap.
- **Own listings are recognised — confirmed.** Own retainers are matched and excluded from the
  competitor set, so the plugin will not chase its own price down.
- **The request throttle** is set to 3000 ms, matching what other plugins recommend. The
  calibration in the debug window can lower it: re-run at shorter intervals until the summary
  line reports lost requests.

- **Sale history is recorded per unit — confirmed**, both from the in-game history window and
  from `HistoryBasisDetector`, which reads the same sales both ways and keeps whichever produces
  consistent prices. The detector still runs and is shown in the debug window, so a change in the
  game's convention would be caught rather than silently inflating prices.

All the observations the plan was waiting on are now settled. Run the calibration, lowering the
  interval and re-running until the summary line reports lost requests; keep the configured
  interval above that. The 3000 ms other plugins recommend also covers waiting for a window to
  draw, which prefetching does not need.

## Licence

AGPL-3.0-or-later, inherited from the template. Quicksell is a modified version of
[goatcorp SamplePlugin][template], modified in 2026; see `LICENSE` for the full terms.
ECommons, vendored as a submodule, is AGPL-3.0 as well.

[template]: https://github.com/goatcorp/SamplePlugin
