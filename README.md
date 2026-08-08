# Quicksell

Dalamud plugin that reprices your retainers' market board listings.

**Still in development — not ready to install or test.** No release, no repository entry, and
the automation is only partly finished.

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

## Layout

- **`Quicksell.Pricing/`** — the decision engine. Plain `net10.0`, no Dalamud dependency, so it
  can be tested without the game running. `PricingEngine.Decide` is a pure function.
- **`Quicksell/`** — the plugin: market data collection, request scheduling, windows.
- **`Quicksell.Pricing.Tests/`** — xUnit coverage of every branch of the decision tree.

## Licence

MIT, see `LICENSE`. ECommons, vendored as a submodule, is MIT too.

The project was scaffolded from the AGPL-3.0 [goatcorp SamplePlugin][template], but every
file it provided has since been replaced, so none of its terms carry over.

[template]: https://github.com/goatcorp/SamplePlugin
