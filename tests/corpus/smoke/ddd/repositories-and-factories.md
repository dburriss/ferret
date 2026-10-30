# Repositories and factories

A repository gives the illusion of an in-memory collection of aggregates. Callers ask for an order by id and save it back; they never see SQL or the ORM.

## Rules

- One repository per aggregate root, not per table.
- The interface belongs to the domain layer, the implementation to infrastructure.

## Factories

When building a valid aggregate is complicated, a factory hides the assembly and guarantees that invariants hold from the first moment the object exists.
