---
name: python-development
description: Safely inspect and use Python environments, virtual environments, packages, and Python-based SDKs without confusing bundled application runtimes with the user's interpreter.
---

# Python development

Before claiming Python, pip, or a package is unavailable, call `inspect_python_environment` and inspect every reported candidate.

- Use the absolute recommended interpreter returned by the tool. Do not fall back to a bare `python`, `python3`, `py`, or `pip` command when more than one interpreter exists.
- Do not install packages into application-bundled runtimes such as LibreOffice, Blender, ArcGIS, or GIMP Python.
- Prefer a project-local `.venv`. If the project has none and package installation is required, explain the proposed environment and create it inside the current project.
- Invoke pip as `"<absolute-python>" -m pip`; never assume a standalone `pip` executable belongs to the same interpreter.
- Check an SDK with `"<absolute-python>" -c "from importlib.metadata import version; print(version('<distribution>'))"`.
- If a Skill links to `references/setup.md` or another relative document, call `read_skill_reference` before deciding how to install or configure its SDK.
- Package installation is not Skill installation. Registering `SKILL.md` does not install Python, an SDK, dependencies, credentials, or command-line programs.
- When a command wrapper exists but its module is missing, treat it as a stale or mismatched launcher. Verify the interpreter and package together before using or replacing it.
