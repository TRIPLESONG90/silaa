---
name: document-office
description: Read or create Word DOCX office documents. Use for reports, specifications, proposals, manuals, meeting notes, and other word-processing deliverables.
---

# Word documents

- Use `read_document` for `.docx`; do not attempt to decode the ZIP package with `read_file`.
- Use `create_document` with format `docx` for a new Word deliverable.
- Treat the first non-empty line as the document title and preserve headings and paragraph breaks.
- Write generated deliverables below the active project unless the user names another allowed folder.
- Never overwrite an existing document unless the user explicitly asks. Read it first when revising.
- After creation, report the exact saved path and briefly verify the extracted text.
