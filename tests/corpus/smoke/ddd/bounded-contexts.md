# Bounded contexts

A model is only valid inside an explicit boundary. A bounded context marks where a particular model and its vocabulary apply. Sales may see a customer as a lead with a pipeline, while billing sees a customer as an account with invoices.

## Rules of thumb

- One team owns one context.
- Do not share a database schema across contexts.
- Integrate through published interfaces, not through shared tables.

## Signals you need a boundary

The same noun keeps acquiring conflicting attributes, a class has dozens of nullable fields, or two teams argue about what a word means.
