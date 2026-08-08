# Quicksell

Dalamud plugin that reprices your retainers' market board listings.

**Still in development — not ready to install or test.** No release, no repository entry, and
the automation is only partly finished.

## Why

The manual loop is: bell → retainer → item → adjust price → compare prices → note the cheapest →
subtract one → paste → confirm → next. Although Penny Pincher plugin could remove the mental arithmetic; everything
else is still hand work, and it does not scale past a couple of retainers.

Quicksell plugin automates the loop.

## Layout

- **`Quicksell.Pricing/`** — the decision engine. Plain `net10.0`, no Dalamud dependency, so it
  can be tested without the game running. `PricingEngine.Decide` is a pure function.
- **`Quicksell/`** — the plugin: market data collection, request scheduling, windows.
- **`Quicksell.Pricing.Tests/`** — xUnit coverage of every branch of the decision tree.

## Licence

MIT, see `LICENSE`. ECommons, vendored as a submodule, is MIT too.
