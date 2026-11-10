---
name: skill-creator
description: Create or update reusable project-local silaa skills when the user wants the Agent to retain a specialized workflow or capability.
---

# Skill creator

Create skills that add useful, non-obvious guidance without constraining unrelated work.

## Design

- Preserve the user's chosen scope, product, permissions, and terminology. A skill supports the task; it does not broaden authority.
- Assume the Agent is already capable. Keep only decision-changing domain knowledge, workflow invariants, tool routing, and real safety constraints.
- Use a short, discriminating lowercase kebab-case name and a one-line description that says what the skill does and when it applies.
- Match specificity to risk. Keep open-ended work flexible; use fixed steps only when deviation would create a concrete failure.
- Keep `SKILL.md` concise. Put substantial conditional guidance in `references/`, reusable deterministic operations in `scripts/`, and output templates in `assets/` only when they have a concrete purpose.
- Do not add README files, changelogs, empty resource folders, copied manuals, or speculative examples.

## Create or update

1. Check `list_local_skills` and inspect an existing skill before updating it.
2. Draft the frontmatter description and focused Markdown instructions.
3. Use `create_or_update_skill`. Set `overwrite` only when the user asked to revise that existing skill.
4. If supporting resources are genuinely needed, create them under `.silaa/skills/<name>/` with the workspace file tools and link them from `SKILL.md` with a clear condition for reading or running them.
5. Run `validate_skill` and fix every reported structural or placeholder problem.

Project-local skills live in `.silaa/skills/<name>/SKILL.md` and become available on the next Agent run. silaa does not require `agents/openai.yaml`; add portability metadata only if the user explicitly needs the skill exported to another harness.

When updating, preserve useful existing instructions and resources unless the requested change makes them obsolete. Never delete a skill or its resources without explicit authorization.
