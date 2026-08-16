#!/usr/bin/env python3
"""Validate DevBuddy knowledge entity metadata without external packages."""
from __future__ import annotations

import argparse
import re
from pathlib import Path

DEFAULT_MEMORY_ROOT = ".devbuddy"
CORE = {"Context.md", "KnowledgeBase.md"}
TYPED = ("domains", "features", "requirements", "flows", "business-rules", "screens", "technical", "tests", "decisions", "releases", "incidents")
FIELDS = {"id", "type", "status", "owner", "source", "project_ids", "last_verified", "confidence"}
KEY_RE = re.compile(r"^(DOM|FEAT|REQ|FLOW|BR|SCR|API|DB|EVT|TEST|ADR|REL|INC)-[A-Za-z0-9][A-Za-z0-9._-]*$")
DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
REFERENCE_RE = re.compile(r"devbuddy-ref:\s*([^\n]+)")
WIKILINK_RE = re.compile(r"\[\[([A-Za-z][A-Za-z0-9._-]*)")


def metadata(path: Path) -> dict[str, str] | None:
    lines = path.read_text(encoding="utf-8").splitlines()
    if not lines or lines[0].strip() != "---":
        return None
    result: dict[str, str] = {}
    for line in lines[1:]:
        if line.strip() == "---":
            return result
        match = re.match(r"^([A-Za-z_][A-Za-z0-9_-]*):\s*(.+)$", line)
        if match:
            result[match.group(1)] = match.group(2).strip()
    return None


def resolve_root(args: argparse.Namespace, parser: argparse.ArgumentParser) -> Path:
    selected = [value for value in (args.memory_root, args.devbuddy_root, args.root, args.project_root) if value is not None]
    if len(selected) != 1:
        parser.error("provide --devbuddy-root, a legacy memory path, --root, or --project-root")
    if args.project_root is not None:
        workspace = (args.project_root / DEFAULT_MEMORY_ROOT).expanduser().resolve()
    else:
        workspace = selected[0].expanduser().resolve()
    if workspace.name != DEFAULT_MEMORY_ROOT:
        parser.error(f"DevBuddy root must be named {DEFAULT_MEMORY_ROOT}: {workspace}")
    knowledge = workspace / "knowledge-base"
    return knowledge if knowledge.is_dir() or not all((workspace / name).is_file() for name in CORE) else workspace


def validate_entity(path: Path, data: dict[str, str], errors: list[str]) -> None:
    absent = FIELDS - set(data)
    if absent:
        errors.append(f"{path}: missing fields: {', '.join(sorted(absent))}")
        return
    empty = sorted(field for field in FIELDS if not data[field].strip())
    if empty:
        errors.append(f"{path}: fields must not be empty: {', '.join(empty)}")
    if not KEY_RE.fullmatch(data["id"]):
        errors.append(f"{path}: invalid knowledge id {data['id']}")
    if not DATE_RE.fullmatch(data["last_verified"]):
        errors.append(f"{path}: last_verified must use YYYY-MM-DD")
    if data["confidence"] not in {"verified", "high", "medium", "low", "unknown"}:
        errors.append(f"{path}: confidence must be verified/high/medium/low/unknown")
    if not re.fullmatch(r"\[[A-Za-z0-9._-]+(?:,\s*[A-Za-z0-9._-]+)*\]", data["project_ids"]):
        errors.append(f"{path}: project_ids must be a non-empty inline list")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("memory_root", nargs="?", type=Path, help="legacy .devbuddy root positional form")
    roots = parser.add_mutually_exclusive_group()
    roots.add_argument("--devbuddy-root", type=Path, help="DevBuddy workspace root")
    roots.add_argument("--root", type=Path, help="legacy alias for a root named .devbuddy")
    roots.add_argument("--project-root", type=Path, help="project root; resolve memory at <project-root>/.devbuddy")
    args = parser.parse_args()
    root = resolve_root(args, parser)
    errors: list[str] = []
    if not root.is_dir():
        print(f"ERROR: memory root not found: {root}")
        return 1
    missing_core = [name for name in sorted(CORE) if not (root / name).is_file()]
    if missing_core:
        errors.append("missing core files: " + ", ".join(missing_core))
    seen: dict[str, Path] = {}
    markdown: list[Path] = []
    count = 0
    for directory in TYPED:
        base = root / directory
        if not base.exists():
            continue
        for path in base.rglob("*.md"):
            markdown.append(path)
            count += 1
            data = metadata(path)
            if data is None:
                errors.append(f"{path}: missing YAML metadata")
                continue
            validate_entity(path, data, errors)
            if FIELDS - set(data):
                continue
            key = data["id"]
            if key in seen:
                errors.append(f"duplicate id {key}: {seen[key]} and {path}")
            else:
                seen[key] = path
    for path in markdown:
        body = path.read_text(encoding="utf-8")
        for match in REFERENCE_RE.finditer(body):
            for key in (value.strip() for value in match.group(1).split(",")):
                if key and key not in seen:
                    errors.append(f"{path}: unresolved devbuddy-ref {key}")
        for key in WIKILINK_RE.findall(body):
            if KEY_RE.fullmatch(key) and key not in seen:
                errors.append(f"{path}: unresolved wiki-link {key}")
    if errors:
        for error in errors:
            print(f"ERROR: {error}")
        return 1
    print(f"OK: {root} has {count} validated typed knowledge entities")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
