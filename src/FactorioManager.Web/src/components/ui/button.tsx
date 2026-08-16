import { cva, type VariantProps } from "class-variance-authority";
import type { ButtonHTMLAttributes } from "react";
import { cn } from "../../lib/utils";

const buttonVariants = cva("inline-flex items-center justify-center rounded-md text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-yellow-400 disabled:pointer-events-none disabled:opacity-50", {
  variants: { variant: { default: "bg-yellow-400 text-zinc-950 hover:bg-yellow-300", secondary: "bg-zinc-700 text-zinc-100 hover:bg-zinc-600", destructive: "bg-red-600 text-white hover:bg-red-500", outline: "border border-zinc-600 text-zinc-100 hover:bg-zinc-800" }, size: { default: "h-10 px-4 py-2", sm: "h-8 px-3", lg: "h-11 px-6" } },
  defaultVariants: { variant: "default", size: "default" }
});

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement>, VariantProps<typeof buttonVariants> {}
export function Button({ className, variant, size, ...props }: ButtonProps) { return <button className={cn(buttonVariants({ variant, size }), className)} {...props} />; }
