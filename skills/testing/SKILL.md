---
name: testing
description: Design focused tests covering normal behavior, boundaries, failures, cancellation, and regressions.
---

# Testing

- Start with the behavior the change promises, then cover boundaries and failures.
- Prefer deterministic tests with isolated state and explicit assertions.
- Test cancellation, persistence, encoding, and concurrency when relevant.
- Run the narrow test first, then the relevant suite and build.
- Do not weaken assertions merely to make a failing test pass.
- Distinguish mocked contract tests from real integration tests.
