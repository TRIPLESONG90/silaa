---
name: document-spreadsheet
description: Read or create Excel XLSX spreadsheets and CSV tables. Use for tabular data, trackers, calculations, inventories, schedules, and exports.
---

# Spreadsheets

- Use `read_document` for `.xlsx` and `.csv` rather than treating an XLSX package as text.
- Use `create_document` with format `xlsx` for a workbook or `csv` for a plain export.
- Supply tabular content as CSV: the first row contains column headers and later rows contain values.
- Keep one fact per cell, stable column names, and explicit units or date formats.
- Write generated deliverables below the active project unless the user names another allowed folder.
- After creation, report the exact saved path and verify the sheet names and representative rows.
