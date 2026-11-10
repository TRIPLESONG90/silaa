---
name: document-presentation
description: Read or create PowerPoint PPTX presentations, including built-in silaa presentation templates. Use for reports, proposals, teaching decks, and briefings.
---

# Presentations

- Use `read_document` for `.pptx`; do not decode the package with `read_file`.
- Use `create_document` with format `pptx` for a new deck.
- Separate slides in content with a line containing only `---`. The first line of each slide is its title; following lines are body bullets.
- Choose a template matching the purpose: `blank`, `business-report`, `project-proposal`, or `training`.
- Keep one message per slide and prefer short bullets over dense paragraphs.
- Write generated decks below the active project unless the user names another allowed folder.
- After creation, report the exact saved path and verify the extracted slide titles.
