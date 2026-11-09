# Review guidance

This is a sanctions screening demo. `CLAUDE.md` at the repository root holds the conventions and design; review
against it. Most important:

- **Fail closed.** A list that failed to load or wasn't searched shows `Not checked`, never a percentage or a clean
  result. A list past its allowed age is still searched, as `Overdue`, with a warning in every result; an
  administrator can switch a list off, and every result then names it as not searched (owner's decisions).
- **Deterministic.** No LLM SDK, network call or randomness in matching, import or the request path.
- **No user input stored or logged.** No request bodies in logs, traces, exceptions or the database.
- **A screening request never touches the database.** Matching runs on in-memory indexes.
- Wording: never "not sanctioned", "cleared", "forgiven" or "absolved"; "No match found in: <lists, as of …>".
- C# style: no underscore prefix on fields, `this.` only when required, **no default arguments** (no analyzer has
  this rule), tests assert behaviour and never log messages.
- Infra is Bicep only (`infra/`): no role assignments outside `bootstrap.bicep`, nothing personal (emails, object
  IDs, IPs) committed.
