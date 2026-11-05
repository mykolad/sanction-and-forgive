# Sanction & Forgive

A demo of sanctions screening for shipments. Enter a receiver, a company and an address, in Latin or Cyrillic
script, on the website or through a JSON API. For each entry and each public sanctions list (EU, OFAC, UN, UK,
Switzerland, Canada, Australia, Ukraine, US export lists), it estimates how likely it is that the entry is the
listed party.

Matching is deterministic and explainable: legal forms and generic words don't count towards similarity, Russian and
Ukrainian names and addresses are transliterated, and surnames with initials (`Шевченко Т.Г.`) are understood.

**This is a demo list check, not a legal determination.** It doesn't cover ownership or control by listed parties, or
embargoes, and a person must review possible matches. Despite the name, nothing here forgives anyone.

Work in progress. Developer guide: [CLAUDE.md](CLAUDE.md). Infrastructure: [infra/README.md](infra/README.md).

Licence: MIT. List data belongs to its publishers; the website names each source.
