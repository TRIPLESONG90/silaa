---
name: document-pdf
description: Read text from PDF files or create a new text-based PDF deliverable. Use when the requested source or output is PDF.
---

# PDF documents

- Use `read_document` for `.pdf`; extracted text may not preserve complex visual layout.
- Use `create_document` with format `pdf` for a new PDF.
- Preserve the source file and flag scanned PDFs when no text can be extracted.
- Prefer concise headings and readable paragraphs because PDF output is paginated.
- Write generated deliverables below the active project unless the user names another allowed folder.
- After creation, report the exact saved path and verify that text can be read back.
