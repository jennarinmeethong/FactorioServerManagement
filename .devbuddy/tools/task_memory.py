#!/usr/bin/env python3
"""Create, coordinate, and validate compact DevBuddy task memory.

This is deliberately dependency-free so the identical tool can ship with both
adapters.  It enforces the protocol at its file boundary; platform identity and
filesystem ACLs remain an adapter-host responsibility.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import tempfile
from pathlib import Path

import bootstrap_knowledge

IDENTIFIER = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
RELATIVE_PATH = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._/-]*$")
REVISION = re.compile(r"^- Memory revision: (\d+)$", re.MULTILINE)
CORE = {"Context.md", "KnowledgeBase.md"}
RECORD_STATUSES = {"completed", "blocked", "failed", "waiting_user"}


def identifier(value: str, label: str) -> str:
    if not IDENTIFIER.fullmatch(value):
        raise ValueError(f"invalid {label}: {value!r}")
    return value


def relative_path(value: str, label: str) -> str:
    cleaned = value.strip().replace("\\", "/").rstrip("/")
    if not cleaned or not RELATIVE_PATH.fullmatch(cleaned) or cleaned.startswith(".") or "/../" in f"/{cleaned}/":
        raise ValueError(f"invalid {label}: {value!r}")
    return cleaned


def qualified_path(value: str, label: str, memory: Path) -> str:
    if ":" not in value:
        raise ValueError(f"{label} must be project-qualified: {value!r}")
    project_id, path = value.split(":", 1)
    identifier(project_id, f"{label} project ID")
    if project_id not in project_registry(memory):
        raise ValueError(f"unknown {label} project ID: {project_id}")
    return f"{project_id}:{relative_path(path, label)}"


def root(args: argparse.Namespace) -> Path:
    selected = [path for path in (args.devbuddy_root, args.root, args.project_root) if path is not None]
    if len(selected) != 1:
        raise ValueError("provide exactly one of --devbuddy-root, --root, or --project-root")
    value = args.devbuddy_root or args.root or args.project_root / ".devbuddy"
    resolved = value.expanduser().resolve()
    if resolved.name != ".devbuddy":
        raise ValueError(f"DevBuddy root must be named .devbuddy: {resolved}")
    if not resolved.is_dir():
        raise ValueError(f"memory root not found: {resolved}")
    missing = sorted(name for name in CORE if not (resolved / "knowledge-base" / name).is_file())
    if missing:
        raise ValueError("memory root is incomplete: " + ", ".join(missing))
    return resolved


def project_registry(memory: Path) -> dict[str, Path]:
    return bootstrap_knowledge.registered_projects(memory)


def task_paths(memory: Path, task_id: str) -> tuple[Path, Path, Path]:
    base = memory / "tasks"
    task_dir = base / f"task-{task_id}"
    return base / f"task-{task_id}.md", task_dir, task_dir / "records"


def atomic_write(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            handle.write(content)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    except OSError:
        Path(temporary).unlink(missing_ok=True)
        raise


def selected_projects(args: argparse.Namespace, memory: Path) -> list[str]:
    values = args.project_id or []
    registry = project_registry(memory)
    unique: list[str] = []
    for value in values:
        project_id = identifier(value, "project ID")
        if registry and project_id not in registry:
            raise ValueError(f"unknown project ID: {project_id}")
        if project_id not in unique:
            unique.append(project_id)
    return unique


def task(args: argparse.Namespace) -> tuple[Path, list[str], str, Path, Path, Path]:
    memory = root(args)
    project_ids = selected_projects(args, memory)
    task_id = identifier(args.task_id, "task ID")
    ledger, task_dir, records = task_paths(memory, task_id)
    return memory, project_ids, task_id, ledger, task_dir, records


def revision(ledger: Path) -> int:
    match = REVISION.search(ledger.read_text(encoding="utf-8"))
    if match is None:
        raise ValueError(f"task ledger has no Memory revision: {ledger}")
    return int(match.group(1))


def require_ledger(ledger: Path) -> None:
    if not ledger.is_file():
        raise ValueError(f"task ledger not found: {ledger}")


def require_owner(actor: str) -> None:
    if actor != "owner":
        raise ValueError("only --actor owner may mutate ledger state")


def initialise(args: argparse.Namespace) -> int:
    memory, project_ids, task_id, ledger, _task_dir, records = task(args)
    if not project_ids:
        raise ValueError("provide at least one --project-id when creating a task")
    session_id = identifier(args.session_id or "session-1", "session ID")
    records.mkdir(parents=True, exist_ok=True)
    if ledger.exists():
        content = ledger.read_text(encoding="utf-8")
        marker = f"- {session_id}\n"
        if marker not in content:
            atomic_write(ledger, content.rstrip() + f"\n\n## Sessions\n{marker}")
        action = "RESUME"
    else:
        atomic_write(ledger, "\n".join((
            f"# Task Ledger: {task_id}", "", "- Status: `queued`", f"- Project IDs: [{', '.join(project_ids)}]",
            f"- Session ID / attempt: {session_id} / 1", "- Memory revision: 0", "- Memory root reference: " + str(memory),
            "", "## Slices, locks, and records", "", "## Approvals and decisions", "", "## Audit references", "",
        )))
        action = "CREATE"
    print(f"{action}: ledger={ledger}")
    print(f"RECORDS: {records}")
    return 0


def require_string(data: dict[str, object], key: str) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"record.{key} must be a non-empty string")
    return value


def require_string_list(data: dict[str, object], key: str, limit: int) -> None:
    value = data.get(key)
    if not isinstance(value, list) or len(value) > limit or any(not isinstance(item, str) or not item.strip() for item in value):
        raise ValueError(f"record.{key} must be a list of at most {limit} non-empty strings")


def validate_record(content: str, task_id: str, slice_id: str, attempt: int, parent_revision: int) -> dict[str, object]:
    try:
        record = json.loads(content)
    except json.JSONDecodeError as error:
        raise ValueError(f"record is not valid JSON: {error.msg}") from None
    if not isinstance(record, dict):
        raise ValueError("record must be a JSON object")
    required = {
        "schema_version", "task_id", "slice_id", "attempt", "parent_revision", "role", "model", "effort", "status",
        "result", "evidence", "next_slice", "knowledge_keys", "knowledge_proposal", "blockers", "required_approval",
    }
    unknown, missing = set(record) - required, required - set(record)
    if unknown or missing:
        detail = "; ".join(filter(None, (
            "unknown: " + ", ".join(sorted(unknown)) if unknown else "",
            "missing: " + ", ".join(sorted(missing)) if missing else "",
        )))
        raise ValueError(f"record fields invalid ({detail})")
    if record["schema_version"] != 1:
        raise ValueError("record.schema_version must be 1")
    if record["task_id"] != task_id or record["slice_id"] != slice_id or record["attempt"] != attempt or record["parent_revision"] != parent_revision:
        raise ValueError("record identity must match task ID, slice ID, attempt, and parent revision")
    for key in ("role", "model", "effort"):
        identifier(require_string(record, key), f"record {key}")
    if record["status"] not in RECORD_STATUSES:
        raise ValueError("record.status must be completed, blocked, failed, or waiting_user")
    require_string(record, "result")
    require_string_list(record, "knowledge_keys", 32)
    require_string_list(record, "blockers", 16)
    proposal, approval = record["knowledge_proposal"], record["required_approval"]
    if proposal is not None and not isinstance(proposal, str):
        raise ValueError("record.knowledge_proposal must be null or a string")
    if approval is not None and not isinstance(approval, str):
        raise ValueError("record.required_approval must be null or a string")
    evidence = record["evidence"]
    if not isinstance(evidence, list) or len(evidence) > 16:
        raise ValueError("record.evidence must be a list of at most 16 entries")
    for item in evidence:
        if not isinstance(item, dict) or set(item) != {"ref", "outcome"}:
            raise ValueError("each record.evidence entry must contain only ref and outcome")
        require_string(item, "ref"); require_string(item, "outcome")
    next_slice = record["next_slice"]
    if not isinstance(next_slice, dict) or set(next_slice) != {"summary", "read_paths", "read_keys"}:
        raise ValueError("record.next_slice must contain only summary, read_paths, and read_keys")
    require_string(next_slice, "summary")
    require_string_list(next_slice, "read_paths", 32); require_string_list(next_slice, "read_keys", 32)
    return record


def record_slice(args: argparse.Namespace) -> int:
    _memory, _project_id, task_id, ledger, task_dir, records = task(args)
    slice_id = identifier(args.slice_id, "slice ID")
    if args.attempt < 1:
        raise ValueError("attempt must be at least 1")
    require_ledger(ledger)
    if args.parent_revision != revision(ledger):
        raise ValueError("stale parent revision; read the latest ledger before writing a record")
    source = args.input.expanduser().resolve()
    if source.suffix != ".json":
        raise ValueError("record input must use a .json file")
    inbox = (task_dir / "inbox").resolve()
    try:
        source.relative_to(inbox)
    except ValueError:
        raise ValueError(f"record input must be below {inbox}") from None
    content = source.read_text(encoding="utf-8")
    record = validate_record(content, task_id, slice_id, args.attempt, args.parent_revision)
    target = records / f"{slice_id}-{args.attempt}.json"
    atomic_write(target, json.dumps(record, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n")
    print(f"OK: record={target}")
    return 0


def reserve(args: argparse.Namespace) -> int:
    memory, _project_id, _task_id, ledger, task_dir, _records = task(args)
    require_ledger(ledger)
    scope = qualified_path(args.scope, "reservation scope", memory)
    if args.expected_revision != revision(ledger):
        raise ValueError("stale expected revision; reservation refused")
    lock = task_dir / "locks" / f"{scope.replace(':', '__').replace('/', '__')}.lock"
    lock.parent.mkdir(parents=True, exist_ok=True)
    try:
        descriptor = os.open(lock, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except FileExistsError:
        raise ValueError(f"reservation already exists: {lock}") from None
    with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
        handle.write(f"actor: {identifier(args.actor, 'actor')}\nscope: {scope}\nrevision: {args.expected_revision}\n")
        handle.flush()
        os.fsync(handle.fileno())
    print(f"OK: reservation={lock}")
    return 0


def release(args: argparse.Namespace) -> int:
    memory, _project_id, _task_id, ledger, task_dir, _records = task(args)
    require_ledger(ledger)
    scope = qualified_path(args.scope, "reservation scope", memory)
    lock = task_dir / "locks" / f"{scope.replace(':', '__').replace('/', '__')}.lock"
    if not lock.is_file():
        raise ValueError(f"reservation not found: {lock}")
    if f"actor: {identifier(args.actor, 'actor')}\n" not in lock.read_text(encoding="utf-8"):
        raise ValueError("reservation belongs to another actor")
    lock.unlink()
    print(f"OK: released={lock}")
    return 0


def commit(args: argparse.Namespace) -> int:
    _memory, _project_id, _task_id, ledger, _task_dir, _records = task(args)
    require_ledger(ledger)
    require_owner(args.actor)
    content = ledger.read_text(encoding="utf-8")
    current = revision(ledger)
    if args.expected_revision != current:
        raise ValueError("stale expected revision; canonical commit refused")
    updated, replacements = REVISION.subn(f"- Memory revision: {current + 1}", content, count=1)
    if replacements != 1:
        raise ValueError("unable to advance memory revision")
    summary = args.summary.strip()
    if not summary or "\n" in summary:
        raise ValueError("summary must be one non-empty line")
    atomic_write(ledger, updated.rstrip() + f"\n\n## Canonical commits\n- revision {current + 1}: {summary}\n")
    print(f"OK: memory revision={current + 1}")
    return 0


def check_scope(args: argparse.Namespace) -> int:
    memory, _project_ids, _task_id, ledger, _task_dir, _records = task(args)
    require_ledger(ledger)
    registry = project_registry(memory)
    scopes: list[tuple[str, str]] = []
    for value in args.write_scope.split(","):
        if ":" not in value:
            raise ValueError(f"write scope must be project-qualified: {value!r}")
        project_id, path = value.split(":", 1)
        identifier(project_id, "scope project ID")
        if project_id not in registry:
            raise ValueError(f"unknown scope project ID: {project_id}")
        scopes.append((project_id, relative_path(path, "write scope")))
    for changed in args.changed:
        if ":" not in changed:
            raise ValueError(f"changed path must be project-qualified: {changed!r}")
        project_id, raw_path = changed.split(":", 1)
        identifier(project_id, "changed project ID")
        if raw_path.replace("\\", "/").strip().rstrip("/") == ".devbuddy" or raw_path.replace("\\", "/").strip().startswith(".devbuddy/"):
            raise ValueError("specialists may not write .devbuddy directly")
        candidate = relative_path(raw_path, "changed path")
        if not any(project_id == scope_project and (candidate == scope or candidate.startswith(scope + "/")) for scope_project, scope in scopes):
            raise ValueError(f"changed path outside write_scope: {project_id}:{candidate}")
    print("OK: changed paths remain within write_scope")
    return 0


def analyse(args: argparse.Namespace) -> int:
    memory, project_ids, _task_id, ledger, task_dir, _records = task(args)
    require_ledger(ledger)
    if len(project_ids) != 1:
        raise ValueError("analyze requires exactly one --project-id")
    source = project_registry(memory)[project_ids[0]]
    if not source.is_dir():
        raise ValueError(f"source root not found: {source}")
    facts = bootstrap_knowledge.discover(source)
    sections = [
        "# Read-only Project Analysis", "", f"- Project ID: `{project_ids[0]}`", f"- Source root: `{source}`", f"- DevBuddy root: `{memory}`",
        f"- Parent revision: {revision(ledger)}", "", "This is a bounded inventory, not canonical knowledge. Owner approval is required before promotion.",
    ]
    for title, key in (
        ("Manifests", "manifests"), ("Package managers", "package_managers"), ("Framework signals", "frameworks"),
        ("Source directories", "source_directories"), ("Test directories", "test_directories"),
        ("Candidate validation commands", "commands"), ("Architecture references", "architecture_references"),
    ):
        values = facts[key]
        sections.extend(("", f"## {title}", ""))
        sections.extend(f"- `{value}`" for value in values) if values else sections.append("- None detected.")
    target = task_dir / "analysis.md"
    atomic_write(target, "\n".join(sections) + "\n")
    print(f"OK: analysis={target}")
    return 0


def validate(args: argparse.Namespace) -> int:
    _memory, _project_id, _task_id, ledger, _task_dir, records = task(args)
    errors = []
    if not ledger.is_file():
        errors.append(f"missing ledger: {ledger}")
    elif REVISION.search(ledger.read_text(encoding="utf-8")) is None:
        errors.append(f"missing memory revision: {ledger}")
    if not records.is_dir():
        errors.append(f"missing record directory: {records}")
    if errors:
        for error in errors:
            print("ERROR: " + error)
        return 1
    print(f"OK: task memory validates for {ledger}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("init", "record", "reserve", "release", "commit", "check-scope", "analyze", "validate"):
        command = commands.add_parser(name)
        roots = command.add_mutually_exclusive_group(required=True)
        roots.add_argument("--devbuddy-root", type=Path)
        roots.add_argument("--root", type=Path)
        roots.add_argument("--project-root", type=Path)
        command.add_argument("--project-id", action="append")
        command.add_argument("--task-id", required=True)
    commands.choices["init"].add_argument("--session-id")
    record = commands.choices["record"]
    record.add_argument("--slice-id", required=True)
    record.add_argument("--attempt", type=int, required=True)
    record.add_argument("--parent-revision", type=int, required=True)
    record.add_argument("--input", type=Path, required=True)
    for name in ("reserve", "release"):
        commands.choices[name].add_argument("--scope", required=True)
        commands.choices[name].add_argument("--actor", required=True)
    commands.choices["reserve"].add_argument("--expected-revision", type=int, required=True)
    commit_command = commands.choices["commit"]
    commit_command.add_argument("--actor", required=True)
    commit_command.add_argument("--expected-revision", type=int, required=True)
    commit_command.add_argument("--summary", required=True)
    scope_command = commands.choices["check-scope"]
    scope_command.add_argument("--write-scope", required=True)
    scope_command.add_argument("--changed", action="append", required=True)
    args = parser.parse_args()
    handlers = {
        "init": initialise, "record": record_slice, "reserve": reserve, "release": release,
        "commit": commit, "check-scope": check_scope, "analyze": analyse, "validate": validate,
    }
    try:
        return handlers[args.command](args)
    except (OSError, ValueError) as error:
        print(f"ERROR: {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
