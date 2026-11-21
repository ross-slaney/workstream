import type { ButtonHTMLAttributes, ReactNode } from "react";
import { cn } from "@/lib/utils";

export type ButtonTone = "primary" | "outline" | "quiet" | "danger";

const TONE: Record<ButtonTone, string> = {
  /** The one main action on a surface. */
  primary: "bg-primary text-primary-foreground hover:opacity-90",
  /** The alternative beside it. */
  outline: "border border-border bg-card text-foreground hover:bg-muted",
  /** An action in a row or a toolbar. */
  quiet: "text-foreground hover:bg-muted",
  danger: "bg-destructive text-destructive-foreground hover:opacity-90",
};

export type ButtonProps = Omit<ButtonHTMLAttributes<HTMLButtonElement>, "children"> & {
  label: string;
  tone?: ButtonTone;
  size?: "default" | "small";
  /** Working on it: presses are ignored and the label says so to a screen reader. */
  busy?: boolean;
  /** Drawn before the label. */
  icon?: ReactNode;
};

/** The app's button on the web. Colors come from `tone`; `className` is for layout only. */
export function Button({ label, tone = "outline", size = "default", busy, icon, className, disabled, type = "button", ...rest }: ButtonProps) {
  return (
    <button
      {...rest}
      type={type}
      disabled={disabled || busy}
      aria-busy={busy || undefined}
      className={cn(
        "inline-flex shrink-0 items-center justify-center gap-2 rounded-md font-medium outline-none transition-opacity",
        "focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1 focus-visible:ring-offset-background",
        "disabled:cursor-not-allowed disabled:opacity-50",
        size === "small" ? "h-8 px-3 text-[13px]" : "h-10 px-4 text-sm",
        TONE[tone],
        className,
      )}
    >
      {icon}
      <span className="truncate">{label}</span>
    </button>
  );
}

export type IconButtonProps = Omit<ButtonHTMLAttributes<HTMLButtonElement>, "children" | "aria-label"> & {
  /** What the button does, for people who cannot see its icon. Also its tooltip. */
  label: string;
  icon: ReactNode;
  /** On the dark stage behind a desktop. */
  onStage?: boolean;
};

/** A button that is only an icon. */
export function IconButton({ label, icon, onStage, className, type = "button", ...rest }: IconButtonProps) {
  return (
    <button
      {...rest}
      type={type}
      aria-label={label}
      title={label}
      className={cn(
        "inline-flex size-8 shrink-0 items-center justify-center rounded-md outline-none",
        "focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50",
        onStage ? "text-stage-foreground hover:bg-white/10" : "text-muted-foreground hover:bg-muted hover:text-foreground",
        className,
      )}
    >
      {icon}
    </button>
  );
}
