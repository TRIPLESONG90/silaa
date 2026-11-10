---
name: automation-design
description: Turn a recurring or background-work request into a safe, observable automation without forcing every workflow into CRUD.
---

# Automation design

- First classify the request as a one-time reminder, independent background task, recurring schedule, reusable skill, or interactive project workflow.
- Use `create_reminder` for delayed one-time alerts. Never keep an LLM request open just to sleep or wait.
- Use `start_background_task` only for self-contained work that genuinely needs an Agent after the foreground turn.
- Use cron only for explicit recurring work. State the local-time schedule, next run, inputs, outputs, and how to pause or remove it.
- Prefer the smallest useful artifact: a script, document workflow, integration, or skill may be better than a database and CRUD UI.
- Make repeated actions idempotent where possible. Define retry limits, duplicate prevention, cancellation, timeout behavior, and a visible result or error.
- Put credentials in Vault and pass only named environment variables to commands. Never copy secret values into prompts, schedules, logs, or source files.
- Confirm which workspace or allowed folder owns generated files, and verify the automation once with a harmless representative input.
