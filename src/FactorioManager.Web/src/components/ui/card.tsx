import type { HTMLAttributes } from "react";
import { cn } from "../../lib/utils";
export function Card({ className, ...props }: HTMLAttributes<HTMLDivElement>) { return <section className={cn("rounded-lg border border-zinc-800 bg-zinc-900 p-5 shadow-sm", className)} {...props} />; }
