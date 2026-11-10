---
name: systematic-debugging
description: Reproduce, isolate, fix the responsible layer, and verify the exact failure path.
---

# Systematic debugging

1. Reproduce the smallest failing case and record the exact error, inputs, versions, and environment.
2. Separate symptoms from the first incorrect state and trace backward to its source.
3. Test one hypothesis at a time. Prefer read-only inspection before changing code.
4. Fix the responsible layer rather than the visible symptom.
5. Re-run the reproduction, nearby regression tests, and the normal build or type-check.
6. Report the cause, the change, and what was actually verified.
