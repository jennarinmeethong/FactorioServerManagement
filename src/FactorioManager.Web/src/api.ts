import type { Action } from "./types";

let csrf = "";
export function setCsrf(value: string): void { csrf = value; }

let onUnauthorized: (() => void) | undefined;
export function setUnauthorizedHandler(handler: (() => void) | undefined): void { onUnauthorized = handler; }

export class ApiError extends Error { constructor(public readonly status: number, message: string) { super(message); this.name = "ApiError"; } }

export function visibleAsync(task: () => Promise<unknown>, onError: (message: string) => void, fallback: string): void {
  void task().catch(caught => onError(caught instanceof Error ? caught.message : fallback));
}

export function invokeAction(action: Action, path: string, init?: RequestInit): void {
  // action owns the shared visible error state; this boundary makes fire-and-forget UI actions explicit.
  void action(path, init).catch(() => undefined);
}

type ErrorPayload = { error?: unknown; detail?: unknown; title?: unknown; message?: unknown; errors?: Record<string, unknown> };

export function formatApiError(status: number, statusText: string, body: string, requestId?: string): string {
  let detail = "";
  try {
    const payload = JSON.parse(body) as ErrorPayload;
    const validation = payload.errors ? Object.entries(payload.errors).flatMap(([field, value]) => (Array.isArray(value) ? value : [value]).filter(Boolean).map(message => `${field}: ${String(message)}`)).join(" ") : "";
    detail = [payload.detail, payload.error, validation, payload.message, payload.title].find(value => typeof value === "string" && value.trim()) as string ?? "";
  } catch { detail = body.replace(/\s+/g, " ").trim(); }
  if (!detail) detail = statusText || `Request failed (${status}).`;
  return `${detail}${requestId ? ` (Request ID: ${requestId})` : ""}`;
}

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  if (init.method && !["GET", "HEAD"].includes(init.method)) headers.set("X-CSRF-Token", csrf);
  if (init.body && !(init.body instanceof FormData)) headers.set("Content-Type", "application/json");
  const response = await fetch(path, { ...init, headers, credentials: "same-origin" });
  if (!response.ok) {
    if (response.status === 401) onUnauthorized?.();
    const body = await response.text().catch(() => "");
    throw new ApiError(response.status, formatApiError(response.status, response.statusText, body, response.headers.get("X-Request-Id") ?? undefined));
  }
  if (response.status === 204) return undefined as T;
  const text = await response.text();
  if (!text.trim()) return undefined as T;
  try { return JSON.parse(text) as T; } catch { throw new ApiError(response.status, "Server returned an invalid JSON response."); }
}
