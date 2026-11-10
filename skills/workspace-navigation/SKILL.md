---
name: workspace-navigation
description: Orient quickly in unfamiliar repositories and user-approved folders without flooding context or crossing access boundaries.
---

# Workspace navigation

- Begin with `list_allowed_directories` so the effective workspace, project, and session scope is explicit.
- Use `glob_files` to find likely manifests, entry points, tests, UI files, and instruction files before listing an entire tree.
- Use `search_text` for concepts and symbols, then `read_file` only around the relevant lines.
- Treat paths outside the reported roots as unavailable. Do not try to bypass the boundary with shell commands, links, or reparse points.
- Before editing, identify the owning layer, its callers, persistence format, and the closest verification path.
- Keep exploration bounded: summarize what each inspected file establishes and stop reading once the change surface is known.
