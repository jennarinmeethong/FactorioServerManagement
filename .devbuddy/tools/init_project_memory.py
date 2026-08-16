#!/usr/bin/env python3
"""Initialise or safely upgrade a multi-project DevBuddy workspace."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
from pathlib import Path

DEFAULT_ROOT = ".devbuddy"
KNOWLEDGE = "knowledge-base"
TOOL_VERSION = "1"
SETTINGS_VERSION = "0.4.5"
PROJECT_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
CORE = {
    "Context.md": "# Technical Context\n\n",
    "KnowledgeBase.md": "# Knowledge Base\n\n",
}
# Keep legacy files during an explicit layout migration, but do not create or
# require them.  Business context belongs in typed business entities and
# decisions in the decisions/ directory, where each item has evidence.
LEGACY_CORE = (*CORE, "BusinessContext.md", "DecisionLog.md")
KNOWLEDGE_DIRS = [
    "domains", "features", "requirements", "flows", "business-rules", "screens",
    "technical/architecture", "technical/apis", "technical/database", "technical/events",
    "technical/integrations", "tests", "decisions", "releases", "incidents",
]
TOOLS = (
    "init_project_memory.py", "bootstrap_knowledge.py", "task_memory.py",
    "validate_project_settings.py", "validate_knowledge.py",
)
# Never copied into a workspace: build output is regenerated locally, and the
# host's real configuration holds credentials the skill must never carry.
SKIP_DIRS = {"bin", "obj", "releases", ".venv", "__pycache__", "node_modules"}
SKIP_FILES = {"appsettings.json", "tool.json", ".DS_Store"}

ALL_ROLES = "[ba-pm, ux-ui, architect, developer, qa, security, devops-sre, dba-data, reviewer]"
STANDARD_ROLES = "[ba-pm, ux-ui, developer, qa, reviewer, devops-sre, dba-data]"


def allowlist_entry(identifier: str, adapter: str, rank: int, roles: str, risks: str) -> tuple[str, ...]:
    return (
        f"    - id: {identifier}",
        f"      adapters: [{adapter}]",
        f"      rank: {rank}",
        f"      allowed_roles: {roles}",
        f"      allowed_risks: {risks}",
    )


DEFAULT_MODELS = (
    *allowlist_entry("claude-haiku-4.5", "claude", 1, STANDARD_ROLES, "[low, medium]"),
    *allowlist_entry("claude-sonnet-5", "claude", 2, ALL_ROLES, "[low, medium, high]"),
    *allowlist_entry("claude-opus-5", "claude", 3, ALL_ROLES, "[high, critical]"),
    *allowlist_entry("claude-fable", "claude", 4, ALL_ROLES, "[critical]"),
    *allowlist_entry("gpt-5.6-luna", "codex", 1, STANDARD_ROLES, "[low, medium]"),
    *allowlist_entry("gpt-5.6-terra", "codex", 2, ALL_ROLES, "[low, medium, high]"),
    *allowlist_entry("gpt-5.6-sol", "codex", 3, ALL_ROLES, "[high, critical]"),
)
DEFAULT_EFFORTS = (
    *allowlist_entry("low", "claude", 1, STANDARD_ROLES, "[low]"),
    *allowlist_entry("medium", "claude", 2, ALL_ROLES, "[low, medium]"),
    *allowlist_entry("high", "claude", 3, ALL_ROLES, "[low, medium, high]"),
    *allowlist_entry("extra", "claude", 4, ALL_ROLES, "[high, critical]"),
    *allowlist_entry("max", "claude", 5, ALL_ROLES, "[high, critical]"),
    *allowlist_entry("ultracode", "claude", 6, ALL_ROLES, "[critical]"),
    *allowlist_entry("light", "codex", 1, STANDARD_ROLES, "[low]"),
    *allowlist_entry("medium", "codex", 2, ALL_ROLES, "[low, medium]"),
    *allowlist_entry("high", "codex", 3, ALL_ROLES, "[low, medium, high]"),
    *allowlist_entry("extra-high", "codex", 4, ALL_ROLES, "[high, critical]"),
    *allowlist_entry("ultra", "codex", 5, ALL_ROLES, "[high, critical]"),
)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def tool_source(name: str) -> Path:
    root = Path(__file__).resolve().parents[1]
    candidates = (
        root / "templates" / "project-tools" / f"{name}.template",
        Path(__file__).resolve().parent / name,
    )
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise ValueError(f"project-tool template not found: {name}")


def bundled_tool(name: str) -> Path:
    """Locate a bundled custom-tool directory under templates/project-tools/.

    These are opt-in: unlike the five Python runtime tools, a bundled custom
    tool may need another runtime and a build step, so seeding it is an explicit
    request rather than part of workspace initialisation.
    """
    if not PROJECT_ID.fullmatch(name):
        raise ValueError(f"invalid custom tool name: {name!r}")
    source = Path(__file__).resolve().parents[1] / "templates" / "project-tools" / name
    if not source.is_dir():
        raise ValueError(f"bundled custom tool not found: {name}")
    return source


def bundled_tool_pairs(source: Path, root: Path) -> list[tuple[Path, Path]]:
    """Plan the copy, skipping anything a build produces or a host owns."""
    pairs: list[tuple[Path, Path]] = []
    for path in sorted(source.rglob("*")):
        if not path.is_file():
            continue
        parts = set(path.relative_to(source).parts)
        if parts & SKIP_DIRS or path.name in SKIP_FILES:
            continue
        pairs.append((path, root / "tools" / path.relative_to(source)))
    return pairs


def workspace_root(args: argparse.Namespace) -> Path:
    if args.devbuddy_root is not None:
        selected = args.devbuddy_root.expanduser().resolve()
    elif args.root is not None:
        print("DEPRECATED: --root is now an alias for --devbuddy-root")
        selected = args.root.expanduser().resolve()
    elif args.project_root is not None:
        print("DEPRECATED: --project-root selects one project; prefer --devbuddy-root and --project id=path")
        selected = args.project_root.expanduser().resolve() / DEFAULT_ROOT
    else:
        selected = (Path.cwd() / DEFAULT_ROOT).resolve()
    if selected.name != DEFAULT_ROOT:
        raise ValueError(f"DevBuddy root must be named {DEFAULT_ROOT}: {selected}")
    return selected


def projects(values: list[str], root: Path, legacy: Path | None) -> dict[str, str]:
    parsed: dict[str, str] = {}
    resolved: set[Path] = set()
    if legacy is not None and not values:
        values = [f"{legacy.expanduser().resolve().name}={legacy}"]
    for value in values:
        if "=" not in value:
            raise ValueError(f"project must use <id>=<path>: {value!r}")
        project_id, raw_path = value.split("=", 1)
        if not PROJECT_ID.fullmatch(project_id):
            raise ValueError(f"invalid project ID: {project_id!r}")
        if project_id in parsed:
            raise ValueError(f"duplicate project ID: {project_id}")
        path = Path(raw_path).expanduser()
        absolute = (path if path.is_absolute() else root.parent / path).resolve()
        if not absolute.is_dir():
            raise ValueError(f"project path not found: {absolute}")
        if absolute == root or root in absolute.parents:
            raise ValueError(f"project path must not point inside the DevBuddy root: {absolute}")
        if absolute in resolved:
            raise ValueError(f"duplicate resolved project path: {absolute}")
        resolved.add(absolute)
        try:
            stored = Path(os.path.relpath(absolute, root.parent)).as_posix()
        except ValueError:
            stored = str(absolute)
        parsed[project_id] = stored
    return parsed


def render_settings(project_map: dict[str, str]) -> str:
    lines = ["schema_version: 1", f"settings_version: {SETTINGS_VERSION}", "workspace:", "  projects:"]
    if not project_map:
        lines.append("    {}")
    for project_id, path in sorted(project_map.items()):
        lines.extend((f"    {project_id}:", f"      path: {path}"))
    lines.extend((
        "memory_root: knowledge-base",
        "tools:",
        "  is_rtk: false",
        "orchestration:",
        "  max_concurrency: 2",
        "  task_timeout_seconds: 900",
        "  retry_limit: 1",
        "  adapter_profiles: [claude, codex]",
        "  approved_models:",
        *DEFAULT_MODELS,
        "  approved_effort_levels:",
        *DEFAULT_EFFORTS,
        "",
    ))
    return "\n".join(lines)


def settings_version(text: str) -> str | None:
    match = re.search(r"^settings_version:\s*([^\s#]+)\s*$", text, re.MULTILINE)
    return match.group(1) if match else None


def section_end(lines: list[str], start: int) -> int:
    for index in range(start + 1, len(lines)):
        if lines[index] and not lines[index].startswith(" "):
            return index
    return len(lines)


def section_start(lines: list[str], name: str) -> int | None:
    for index, line in enumerate(lines):
        if line == f"  {name}:":
            return index
    return None


def entry_ids(lines: list[str]) -> set[str]:
    return {
        match.group(1)
        for line in lines
        if (match := re.match(r"^    - id:\s*([A-Za-z0-9._-]+)\s*$", line))
    }


def merge_section(lines: list[str], name: str, defaults: tuple[str, ...]) -> None:
    start = section_start(lines, name)
    if start is None:
        orchestration = next((index for index, line in enumerate(lines) if line == "orchestration:"), None)
        if orchestration is None:
            lines.extend(("orchestration:", f"  {name}:"))
            lines.extend(defaults)
            return
        end = section_end(lines, orchestration)
        lines[end:end] = [f"  {name}:", *defaults]
        return
    end = section_end(lines, start)
    present = entry_ids(lines[start:end])
    pending: list[str] = []
    for index, line in enumerate(defaults):
        if line.startswith("    - id:") and line.split(": ", 1)[1] in present:
            continue
        if line.startswith("    - id:"):
            pending.extend(defaults[index:index + 5])
    lines[end:end] = pending


def merge_default_settings(text: str) -> str:
    """Add current defaults without replacing any existing workspace choice."""
    lines = text.splitlines()
    if not any(line.startswith("schema_version:") for line in lines):
        lines.insert(0, "schema_version: 1")
    version_line = next((index for index, line in enumerate(lines) if line.startswith("settings_version:")), None)
    if version_line is None:
        schema_line = next(index for index, line in enumerate(lines) if line.startswith("schema_version:"))
        lines.insert(schema_line + 1, f"settings_version: {SETTINGS_VERSION}")
    else:
        lines[version_line] = f"settings_version: {SETTINGS_VERSION}"
    orchestration = next((index for index, line in enumerate(lines) if line == "orchestration:"), None)
    if orchestration is None:
        lines.append("orchestration:")
        orchestration = len(lines) - 1
    defaults = (
        ("max_concurrency", "2"),
        ("task_timeout_seconds", "900"),
        ("retry_limit", "1"),
        ("adapter_profiles", "[claude, codex]"),
    )
    end = section_end(lines, orchestration)
    present = {match.group(1) for line in lines[orchestration:end] if (match := re.match(r"^  ([A-Za-z_][A-Za-z0-9_-]*):", line))}
    lines[end:end] = [f"  {key}: {value}" for key, value in defaults if key not in present]
    merge_section(lines, "approved_models", DEFAULT_MODELS)
    merge_section(lines, "approved_effort_levels", DEFAULT_EFFORTS)
    tools = next((index for index, line in enumerate(lines) if line == "tools:"), None)
    if tools is None:
        lines.extend(("tools:", "  is_rtk: false"))
    elif not any(line.startswith("  is_rtk:") for line in lines[tools:section_end(lines, tools)]):
        lines.insert(section_end(lines, tools), "  is_rtk: false")
    return "\n".join(lines) + "\n"


def settings_projects(path: Path) -> dict[str, str]:
    result: dict[str, str] = {}
    current: str | None = None
    for raw in path.read_text(encoding="utf-8").splitlines():
        if match := re.match(r"^    ([A-Za-z0-9][A-Za-z0-9._-]*):$", raw):
            current = match.group(1)
        elif current and (match := re.match(r"^      path:\s*(.+?)\s*$", raw)):
            result[current] = match.group(1).strip("\"'")
    return result


def migration_pairs(root: Path) -> list[tuple[Path, Path]]:
    names = list(LEGACY_CORE) + [directory.split("/", 1)[0] for directory in KNOWLEDGE_DIRS]
    return [(root / name, root / KNOWLEDGE / name) for name in sorted(set(names)) if (root / name).exists()]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--devbuddy-root", type=Path, help="selected .devbuddy workspace root")
    parser.add_argument("--project", action="append", default=[], metavar="ID=PATH", help="register a source project; repeatable")
    parser.add_argument("--root", type=Path, help=argparse.SUPPRESS)
    parser.add_argument("--project-root", type=Path, help=argparse.SUPPRESS)
    parser.add_argument("--dry-run", action="store_true", help="show planned changes only")
    parser.add_argument("--upgrade-settings", action="store_true",
                        help="fill missing current defaults in an older settings.yaml without overwriting existing values")
    parser.add_argument("--upgrade-tools", action="store_true", help="safely replace recognized project tools")
    parser.add_argument("--migrate-layout", action="store_true", help="move the legacy memory layout below knowledge-base")
    parser.add_argument("--seed-custom-tool", action="append", default=[], metavar="NAME",
                        help="copy a bundled custom tool from templates/project-tools/NAME into tools/; repeatable")
    args = parser.parse_args()
    try:
        root = workspace_root(args)
        project_map = projects(args.project, root, args.project_root)
        sources = {name: tool_source(name) for name in TOOLS}
        bundled = [(name, bundled_tool(name)) for name in args.seed_custom_tool]
    except (OSError, ValueError) as error:
        print(f"ERROR: {error}")
        return 1

    moves = migration_pairs(root) if args.migrate_layout else []
    migration_destinations = {destination for _source, destination in moves}
    settings = root / "settings.yaml"
    settings_content = render_settings(project_map)
    settings_upgrade: str | None = None
    creates: list[tuple[Path, str]] = []
    directories = [root / KNOWLEDGE / item for item in KNOWLEDGE_DIRS] + [root / "tasks", root / "tools"]
    for name, content in CORE.items():
        target = root / KNOWLEDGE / name
        if not target.exists() and target not in migration_destinations:
            creates.append((target, content))
    if not settings.exists():
        creates.append((settings, settings_content))
    elif project_map and settings_projects(settings) != project_map:
        print(f"ERROR: settings already exists with different project registry: {settings}")
        return 1
    elif args.upgrade_settings and settings_version(settings.read_text(encoding="utf-8")) != SETTINGS_VERSION:
        settings_upgrade = merge_default_settings(settings.read_text(encoding="utf-8"))

    conflicts = [destination for source, destination in moves if destination.exists()]
    if conflicts:
        print("ERROR: migration destinations already exist: " + ", ".join(map(str, conflicts)))
        return 1

    manifest_path = root / "tools" / "manifest.json"
    old_manifest: dict[str, object] = {}
    if manifest_path.is_file():
        try:
            old_manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            print(f"ERROR: invalid tool manifest: {manifest_path}")
            return 1
    old_hashes = old_manifest.get("files", {}) if isinstance(old_manifest.get("files", {}), dict) else {}
    tool_actions: list[tuple[str, Path, Path]] = []
    for name, source in sources.items():
        target = root / "tools" / name
        if not target.exists():
            tool_actions.append(("CREATE", source, target))
        elif args.upgrade_tools and sha256(target) != sha256(source):
            if old_hashes.get(name) != sha256(target):
                print(f"ERROR: refusing to replace modified or unrecognized project tool: {target}")
                return 1
            tool_actions.append(("REPLACE", source, target))
    custom_actions: list[tuple[Path, Path]] = []
    for name, source in bundled:
        pairs = bundled_tool_pairs(source, root)
        existing = [target for _source, target in pairs if target.exists()]
        if existing:
            # The host may have edited the seeded copy or built inside it, so an
            # overwrite here could destroy local work the manifest cannot vouch for.
            print(f"ERROR: refusing to overwrite existing custom tool files for {name}: " + ", ".join(map(str, existing[:3])))
            return 1
        custom_actions.extend(pairs)

    new_manifest = {"tool_version": TOOL_VERSION, "files": {name: sha256(source) for name, source in sources.items()}}
    manifest_content = json.dumps(new_manifest, indent=2, sort_keys=True) + "\n"
    write_manifest = not manifest_path.exists() or (args.upgrade_tools and manifest_path.read_text(encoding="utf-8") != manifest_content)

    for directory in directories:
        if not directory.exists():
            print(f"CREATE DIR: {directory}")
    for source, destination in moves:
        print(f"MOVE: {source} -> {destination}")
    for path, _content in creates:
        print(f"CREATE: {path}")
    if settings_upgrade is not None:
        print(f"UPGRADE SETTINGS: {settings}")
    for action, _source, target in tool_actions:
        print(f"{action}: {target}")
    for _source, target in custom_actions:
        print(f"CREATE: {target}")
    if write_manifest:
        print(f"{'REPLACE' if manifest_path.exists() else 'CREATE'}: {manifest_path}")
    if args.dry_run:
        print("DRY RUN: no files written")
        return 0
    try:
        root.mkdir(parents=True, exist_ok=True)
        for source, destination in moves:
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(source), str(destination))
        for directory in directories:
            directory.mkdir(parents=True, exist_ok=True)
        for path, content in creates:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8")
        if settings_upgrade is not None:
            settings.write_text(settings_upgrade, encoding="utf-8")
        for _action, source, target in tool_actions:
            shutil.copy2(source, target)
        for source, target in custom_actions:
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
        if write_manifest:
            manifest_path.write_text(manifest_content, encoding="utf-8")
    except OSError as error:
        print(f"ERROR: cannot initialise DevBuddy workspace at {root}: {error}")
        return 1
    print(f"OK: DevBuddy workspace ready at {root}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
