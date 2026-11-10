---
name: code-review
description: Perform a defect-first review and report concrete actionable findings with evidence and severity.
---

# Code review

Adapted from Codex and Claude Code review workflows.

Review the complete requested diff and enough surrounding code to understand each changed path. Flag an issue only when it affects correctness, security, performance, or maintainability in a meaningful and demonstrable way.

Order findings by severity. Cite the smallest useful file and line, explain the failing scenario, and state why the behavior is wrong. Do not report style preferences or speculative risks. If no qualifying defects exist, say so and mention material test gaps separately.
