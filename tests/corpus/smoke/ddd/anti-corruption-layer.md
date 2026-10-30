# Anti-corruption layer

When your system has to talk to a legacy platform or a third-party API, its model should not leak into yours. An anti-corruption layer is a translation boundary: adapters and facades convert foreign concepts into your own terms at the edge.

## Example

A supplier feed uses cryptic codes and a flat structure. The layer maps it into proper product and price concepts, so the rest of the code never sees the supplier's field names.

## Cost

It is extra code, but it is cheaper than letting a legacy model slowly rewrite your design.
