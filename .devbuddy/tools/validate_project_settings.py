#!/usr/bin/env python3
"""Validate a DevBuddy project settings YAML against the restricted schema, without packages.

This tool is shared verbatim by every adapter and is copied into each
workspace as .devbuddy/tools/validate_project_settings.py, so it must stay
platform-neutral and dependency-free.
"""
from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

ROLES = {"ba-pm", "ux-ui", "architect", "developer", "qa", "security", "devops-sre", "dba-data", "reviewer"}
RISKS = {"low", "medium", "high", "critical"}
SCALARS = {"max_concurrency", "task_timeout_seconds", "retry_limit"}
ADAPTERS = {"claude", "codex"}
SETTINGS_VERSION = "0.4.5"
IDENTIFIER = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
# A manifest is committable by design, so a credential-shaped assignment in one
# is a leak that has already happened rather than a style problem.
CREDENTIAL = re.compile(r"(password|pwd|accountkey|api[_-]?key|secret|token)\s*[=:]\s*[^\s\"',}]+", re.IGNORECASE)


def list_values(value: str) -> set[str]:
    if not (value.startswith("[") and value.endswith("]")):
        return set()
    return {part.strip() for part in value[1:-1].split(",") if part.strip()}


def parse(path: Path) -> tuple[dict[str, str], dict[str, list[dict[str, str]]], dict[str, str], list[str]]:
    scalars: dict[str, str] = {}
    groups = {"approved_models": [], "approved_effort_levels": [], "custom_tools": []}
    errors: list[str] = []
    projects: dict[str, str] = {}
    group: str | None = None
    entry: dict[str, str] | None = None
    project_id: str | None = None
    tool: dict[str, str] | None = None
    for number, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not raw.strip() or raw.lstrip().startswith("#"):
            continue
        if raw == "schema_version: 1":
            scalars["schema_version"] = "1"
            continue
        settings_version_match = re.match(r"^settings_version:\s*([^\s#]+)\s*$", raw)
        if settings_version_match:
            scalars["settings_version"] = settings_version_match.group(1)
            continue
        project_match = re.match(r"^    ([A-Za-z0-9][A-Za-z0-9._-]*):$", raw)
        if project_match:
            project_id = project_match.group(1)
            continue
        path_match = re.match(r"^      path:\s*(.+?)\s*$", raw)
        if path_match and project_id:
            if project_id in projects:
                errors.append(f"line {number}: duplicate project ID {project_id}")
            projects[project_id] = path_match.group(1).strip("\"'")
            continue
        memory_match = re.match(r"^memory_root:\s*(.+?)\s*$", raw)
        if memory_match:
            scalars["memory_root"] = memory_match.group(1)
            continue
        if raw == "memory_root:":
            errors.append(f"line {number}: memory_root must not be empty")
            continue
        if raw in {"orchestration:", "tools:"}:
            continue
        if raw in {"workspace:", "  projects:"}:
            continue
        if raw == "custom_tools:":
            group, entry, tool = "custom_tools", None, None
            continue
        runtimes_match = re.match(r"^  approved_custom_tool_runtimes:\s*(\[.*\])\s*$", raw)
        if runtimes_match:
            scalars["approved_custom_tool_runtimes"] = runtimes_match.group(1)
            continue
        is_rtk_match = re.match(r"^  is_rtk:\s*(true|false)\s*$", raw)
        if is_rtk_match:
            scalars["is_rtk"] = is_rtk_match.group(1)
            continue
        if re.match(r"^  is_rtk:\s*", raw):
            errors.append(f"line {number}: tools.is_rtk must be true or false")
            continue
        profiles_match = re.match(r"^  adapter_profiles:\s*(\[.*\])\s*$", raw)
        if profiles_match:
            scalars["adapter_profiles"] = profiles_match.group(1)
            continue
        tool_match = re.match(r"^  - name:\s*(.+?)\s*$", raw)
        if tool_match and group == "custom_tools":
            tool = {"name": tool_match.group(1).strip("\"'")}
            groups["custom_tools"].append(tool)
            continue
        tool_field_match = re.match(r"^    (runtime|manifest|secret_file):\s*(.+?)\s*$", raw)
        if tool_field_match and tool is not None:
            tool[tool_field_match.group(1)] = tool_field_match.group(2).strip("\"'")
            continue
        group_match = re.match(r"^  (approved_models|approved_effort_levels):$", raw)
        if group_match:
            group, entry, tool = group_match.group(1), None, None
            continue
        scalar_match = re.match(r"^  (max_concurrency|task_timeout_seconds|retry_limit):\s*(\d+)\s*$", raw)
        if scalar_match:
            scalars[scalar_match.group(1)] = scalar_match.group(2)
            continue
        item_match = re.match(r"^    - id:\s*([A-Za-z0-9._-]+)\s*$", raw)
        if item_match and group:
            entry = {"id": item_match.group(1)}
            groups[group].append(entry)
            continue
        field_match = re.match(r"^      (rank|allowed_roles|allowed_risks|adapters):\s*(.+?)\s*$", raw)
        if field_match and entry is not None:
            entry[field_match.group(1)] = field_match.group(2)
            continue
        if raw.startswith(" "):
            errors.append(f"line {number}: unsupported restricted-YAML shape")
    return scalars, groups, projects, errors


def validate_entries(kind: str, entries: list[dict[str, str]], profiles: set[str], errors: list[str]) -> None:
    if not entries:
        errors.append(f"{kind} must contain at least one entry")
        return
    ids: dict[str, set[str]] = {profile: set() for profile in profiles}
    ranks: dict[str, set[int]] = {profile: set() for profile in profiles}
    covered: dict[str, int] = {profile: 0 for profile in profiles}
    for entry in entries:
        missing = {"id", "rank", "allowed_roles", "allowed_risks"} - set(entry)
        if missing:
            errors.append(f"{kind} {entry.get('id', '<unknown>')}: missing " + ", ".join(sorted(missing)))
            continue
        tagged = list_values(entry.get("adapters", ""))
        if profiles:
            if not tagged:
                errors.append(f"{kind} {entry['id']}: adapters is required when adapter_profiles is set")
                continue
            if not tagged <= profiles:
                errors.append(f"{kind} {entry['id']}: adapters must be declared in orchestration.adapter_profiles")
                continue
            targets = tagged
        else:
            # Legacy untagged entries stay universal. Tagged entries are still
            # provider-specific, so an explicit settings upgrade can safely add
            # current defaults without changing old custom entries' meaning.
            targets = tagged or set(ADAPTERS)
        for profile in targets:
            covered[profile] = covered.get(profile, 0) + 1
            if entry["id"] in ids.setdefault(profile, set()):
                errors.append(f"{kind}: duplicate id {entry['id']} for {profile}")
            ids[profile].add(entry["id"])
        if not entry["rank"].isdigit() or int(entry["rank"]) < 1:
            errors.append(f"{kind} {entry['id']}: rank must be a positive integer")
        else:
            for profile in targets:
                if int(entry["rank"]) in ranks.setdefault(profile, set()):
                    errors.append(f"{kind}: duplicate rank {entry['rank']} for {profile}")
                else:
                    ranks[profile].add(int(entry["rank"]))
        roles, risks = list_values(entry["allowed_roles"]), list_values(entry["allowed_risks"])
        if not roles or not roles <= ROLES:
            errors.append(f"{kind} {entry['id']}: allowed_roles must contain canonical roles")
        if not risks or not risks <= RISKS:
            errors.append(f"{kind} {entry['id']}: allowed_risks must contain low/medium/high/critical")
    for profile in profiles:
        if not covered[profile]:
            errors.append(f"{kind} must contain at least one entry for adapter profile {profile}")


def validate_custom_tools(tools: list[dict[str, str]], approved: set[str], root: Path, errors: list[str]) -> None:
    """Check the workspace custom-tool registry.

    An unregistered executable has no approved runtime, no schema, and no
    declared secret boundary, so the registry is what makes calling a tool a
    decision the user already made rather than one the Orchestrator invents.
    """
    if not tools:
        return
    if not approved:
        errors.append("custom_tools requires tools.approved_custom_tool_runtimes")
        return
    names: set[str] = set()
    for tool in tools:
        name = tool.get("name", "<unknown>")
        missing = {"name", "runtime", "manifest"} - set(tool)
        if missing:
            errors.append(f"custom_tools {name}: missing " + ", ".join(sorted(missing)))
            continue
        if not IDENTIFIER.fullmatch(name):
            errors.append(f"custom_tools: invalid tool name {name}")
        if name in names:
            errors.append(f"custom_tools: duplicate tool name {name}")
        names.add(name)
        if tool["runtime"] not in approved:
            errors.append(
                f"custom_tools {name}: runtime '{tool['runtime']}' is not in "
                "tools.approved_custom_tool_runtimes"
            )
        manifest = root / tool["manifest"]
        if not manifest.is_file():
            errors.append(f"custom_tools {name}: manifest not found: {manifest}")
            continue
        text = manifest.read_text(encoding="utf-8", errors="replace")
        try:
            data = json.loads(text)
        except json.JSONDecodeError as error:
            errors.append(f"custom_tools {name}: manifest is not valid JSON: {error}")
            continue
        for key in ("name", "description", "command", "inputSchema", "outputSchema"):
            if key not in data:
                errors.append(f"custom_tools {name}: manifest missing {key}")
        if data.get("name") != name:
            errors.append(f"custom_tools {name}: manifest name is {data.get('name')!r}")
        leak = CREDENTIAL.search(text)
        if leak:
            errors.append(f"custom_tools {name}: manifest looks like it contains a credential ({leak.group(1)})")
        secret = tool.get("secret_file")
        if not secret:
            continue
        if (root / secret).resolve() == manifest.resolve():
            errors.append(f"custom_tools {name}: secret_file must not be the manifest")
        # A committed template is how the required shape stays documented once
        # the real file is git-ignored; without one the next host has to guess.
        stem = Path(secret)
        template = root / stem.with_suffix(f".template{stem.suffix}")
        if not template.is_file():
            errors.append(f"custom_tools {name}: missing committed template beside secret_file: {template}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("settings", type=Path)
    args = parser.parse_args()
    if not args.settings.is_file():
        print(f"ERROR: settings file not found: {args.settings}")
        return 1
    scalars, groups, projects, errors = parse(args.settings)
    if scalars.get("schema_version") != "1":
        errors.append("schema_version must be 1")
    if scalars.get("settings_version") != SETTINGS_VERSION:
        errors.append(
            f"settings_version must be {SETTINGS_VERSION}; run init_project_memory.py --upgrade-settings to fill missing defaults"
        )
    if scalars.get("memory_root", "").strip("\"'") != "knowledge-base":
        errors.append("memory_root must be knowledge-base")
    if not projects:
        errors.append("workspace.projects must contain at least one project")
    resolved: set[Path] = set()
    for project_id, value in projects.items():
        path = Path(value).expanduser()
        absolute = (path if path.is_absolute() else args.settings.parent.parent / path).resolve()
        if not absolute.is_dir():
            errors.append(f"workspace project path not found ({project_id}): {absolute}")
        if absolute in resolved:
            errors.append(f"workspace projects resolve to duplicate path: {absolute}")
        resolved.add(absolute)
    for key in SCALARS:
        if key not in scalars:
            errors.append(f"missing orchestration.{key}")
    if "max_concurrency" in scalars and int(scalars["max_concurrency"]) < 1:
        errors.append("max_concurrency must be at least 1")
    if "task_timeout_seconds" in scalars and int(scalars["task_timeout_seconds"]) < 1:
        errors.append("task_timeout_seconds must be at least 1")
    profiles = list_values(scalars.get("adapter_profiles", ""))
    if profiles and not profiles <= ADAPTERS:
        errors.append("orchestration.adapter_profiles may contain only claude and codex")
    validate_entries("approved_models", groups["approved_models"], profiles, errors)
    validate_entries("approved_effort_levels", groups["approved_effort_levels"], profiles, errors)
    validate_custom_tools(
        groups["custom_tools"],
        list_values(scalars.get("approved_custom_tool_runtimes", "")),
        args.settings.parent,
        errors,
    )
    if errors:
        for error in errors:
            print(f"ERROR: {error}")
        return 1
    print(f"OK: {args.settings} has valid dispatch settings")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
