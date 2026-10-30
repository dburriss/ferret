# Entities and value objects

An entity is defined by identity that persists while its attributes change: a person stays the same person after moving house. A value object is defined purely by its attributes and is immutable, like money, a date range or an address.

## Why prefer value objects

They are easy to test, safe to share, and carry their own validation. Replacing a bare `decimal` with `Money` removes a whole class of currency mistakes.

## Equality

Entities compare by id. Value objects compare by all their fields, which in F# a record gives you for free.
