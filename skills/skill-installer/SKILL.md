---
name: skill-installer
description: Install or update reusable silaa Skills from a local folder or GitHub when the user asks to add capabilities, import a course lab, or use a SKILL.md package.
---

# Skill installation

Use the native `install_skill` tool instead of copying packages with shell commands.

- Prefer `project` scope so the Skill stays with the current work. Use `global` only when the user asks to use it across projects.
- A GitHub repository URL needs the repository-relative folder containing `SKILL.md`. A `/tree/<branch>/<folder>` URL already carries that folder.
- Let the native tool request exact network or folder access. Do not bypass a denied request.
- Set `overwrite` only when the user explicitly asks to update or replace an installed Skill.
- Installation may copy `scripts/`, `references/`, and `assets/`, but it never executes package scripts. Do not run them merely to finish installation.
- After success, state the installed scope and tell the user it becomes available on the next Agent run.
