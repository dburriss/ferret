# Ubiquitous language

Developers and business experts should speak one language, and that language should appear in the code. If the warehouse people say "consignment" and the code says `ShipmentBatch`, every conversation needs a translation step and bugs hide in the gap.

## Practices

- Name classes and methods after terms the experts actually use.
- When the experts' word changes, rename the code.
- Keep a glossary next to the code, and challenge vague words like "process" or "manage".

## Scope

The language is only consistent inside one boundary. The same word, such as "customer", can legitimately mean different things in sales and in billing.
