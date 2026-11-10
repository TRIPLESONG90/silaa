---
name: git-safety
description: Work in Git repositories without overwriting unrelated user changes or rewriting history implicitly.
---

# Git safety

- Check status before editing and inspect overlapping changes.
- Preserve unrelated modified and untracked files.
- Do not use hard reset, forced checkout, clean, force push, or history rewrites unless explicitly requested.
- Keep commits focused when a commit is requested; never commit secrets or generated caches.
- Explain any destructive or difficult-to-recover operation before performing it.
