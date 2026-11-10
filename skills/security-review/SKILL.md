---
name: security-review
description: Review credentials, commands, file access, network calls, and untrusted input using concrete exploit paths.
---

# Security review

Map data flow from untrusted inputs to privileged effects. Validate exact filesystem targets, avoid accidental shell interpolation, keep secrets out of logs and configuration, and bind credentials to the intended endpoint. Review redirects, path traversal, injection, unsafe deserialization, and cancellation cleanup.

Only report a vulnerability when the affected path and attacker-controlled value are concrete. Include impact and the smallest effective mitigation.
