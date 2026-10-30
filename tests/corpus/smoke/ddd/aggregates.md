# Aggregates

An aggregate is a cluster of objects that must change together to keep a business rule true. One member is the aggregate root, and outside code may only hold a reference to the root.

## Consistency boundary

All invariants inside the aggregate are enforced in a single transaction. Anything outside the boundary is eventually consistent. This is why an `Order` can protect "total must equal the sum of lines" but cannot guarantee stock levels held elsewhere.

## Sizing

Prefer small aggregates. Reference other aggregates by identity rather than by object reference, and load only what the transaction needs.
