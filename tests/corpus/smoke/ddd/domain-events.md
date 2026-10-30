# Domain events

A domain event records something that happened that the business cares about, named in the past tense: `OrderPlaced`, `PaymentReceived`. Events are facts, so they are immutable.

## Uses

- Let other parts of the system react without the publisher knowing about them.
- Keep aggregates small by moving side effects into handlers.
- Provide an audit trail, and in event sourcing the events are the source of truth.

## Naming

If you cannot name the event in the experts' own words, the model is probably not understood yet.
