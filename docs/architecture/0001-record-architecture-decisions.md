# ADR 0001: Record architecture decisions

- Status: Accepted
- Date: 2026-10-03

## Context

Aethera is built by several contributors (human and automated) working in parallel. Decisions about contracts, stack and conventions need a durable, reviewable home so nobody has to rediscover them.

## Decision

We record significant architecture decisions as numbered Architecture Decision Records (ADRs) in `docs/architecture/`, named `NNNN-short-title.md`. ADRs are immutable once accepted; a change of direction is a new ADR that supersedes the old one.

Contracts (`proto/`, database migrations, API conventions) are owned by the orchestrator and changed only through an ADR or an explicitly assigned work package.

## Template

```markdown
# ADR NNNN: Title

- Status: Proposed | Accepted | Superseded by NNNN
- Date: YYYY-MM-DD

## Context
## Decision
## Consequences
```

## Index

| ADR | Title | Status |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |

## Consequences

Decisions are discoverable and reviewable in pull requests. Small implementation details do not need an ADR.
