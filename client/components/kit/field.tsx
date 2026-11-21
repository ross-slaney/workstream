import { useId, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from "react";
import { cn } from "@/lib/utils";

const control =
  "w-full rounded-md border border-input bg-background px-3 text-sm text-foreground outline-none placeholder:text-muted-foreground " +
  "focus-visible:border-ring focus-visible:ring-2 focus-visible:ring-ring/30 disabled:cursor-not-allowed disabled:opacity-60";

type Labelled = {
  label: string;
  /** Said under the field: what it is for. */
  hint?: string;
  /** Why the value was refused, in the server's words. */
  error?: string | null;
};

function Frame({ id, label, hint, error, children }: Labelled & { id: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-[13px] font-medium text-foreground">
        {label}
      </label>
      {children}
      {error ? (
        <p id={`${id}-error`} className="text-[13px] text-destructive">
          {error}
        </p>
      ) : hint ? (
        <p id={`${id}-hint`} className="text-[13px] text-muted-foreground">
          {hint}
        </p>
      ) : null}
    </div>
  );
}

const describedBy = (id: string, hint?: string, error?: string | null) => (error ? `${id}-error` : hint ? `${id}-hint` : undefined);

export type TextFieldProps = Labelled & Omit<InputHTMLAttributes<HTMLInputElement>, "onChange" | "value"> & {
  value: string;
  onChangeText: (value: string) => void;
};

export function TextField({ label, hint, error, value, onChangeText, className, ...rest }: TextFieldProps) {
  const id = useId();
  return (
    <Frame id={id} label={label} hint={hint} error={error}>
      <input
        {...rest}
        id={id}
        value={value}
        onChange={(event) => onChangeText(event.target.value)}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(id, hint, error)}
        className={cn(control, "h-10", className)}
      />
    </Frame>
  );
}

export type TextAreaProps = Labelled & Omit<TextareaHTMLAttributes<HTMLTextAreaElement>, "onChange" | "value"> & {
  value: string;
  onChangeText: (value: string) => void;
  /** Written in a fixed-width face: a script. */
  code?: boolean;
};

export function TextArea({ label, hint, error, value, onChangeText, code, className, rows = 4, ...rest }: TextAreaProps) {
  const id = useId();
  return (
    <Frame id={id} label={label} hint={hint} error={error}>
      <textarea
        {...rest}
        id={id}
        rows={rows}
        value={value}
        onChange={(event) => onChangeText(event.target.value)}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(id, hint, error)}
        spellCheck={code ? false : rest.spellCheck}
        className={cn(control, "resize-y py-2 leading-relaxed", code && "font-mono text-[13px]", className)}
      />
    </Frame>
  );
}

export type SelectFieldProps = Labelled & Omit<SelectHTMLAttributes<HTMLSelectElement>, "onChange" | "value"> & {
  value: string;
  onChangeValue: (value: string) => void;
  options: { value: string; label: string }[];
};

export function SelectField({ label, hint, error, value, onChangeValue, options, className, ...rest }: SelectFieldProps) {
  const id = useId();
  return (
    <Frame id={id} label={label} hint={hint} error={error}>
      <select
        {...rest}
        id={id}
        value={value}
        onChange={(event) => onChangeValue(event.target.value)}
        aria-describedby={describedBy(id, hint, error)}
        className={cn(control, "h-10", className)}
      >
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    </Frame>
  );
}

export type CheckFieldProps = {
  label: string;
  description?: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
};

/** A checkbox with its words, the whole row clickable. */
export function CheckField({ label, description, checked, onChange, disabled }: CheckFieldProps) {
  const id = useId();
  return (
    <label htmlFor={id} className={cn("flex cursor-pointer items-start gap-3 rounded-md border border-border p-3 hover:bg-muted", checked && "border-primary bg-primary-soft hover:bg-primary-soft", disabled && "cursor-not-allowed opacity-60")}>
      <input id={id} type="checkbox" checked={checked} disabled={disabled} onChange={(event) => onChange(event.target.checked)} className="mt-0.5 size-4 accent-[rgb(var(--primary))]" />
      <span className="flex min-w-0 flex-col">
        <span className="text-sm font-medium text-foreground">{label}</span>
        {description ? <span className="text-[13px] text-muted-foreground">{description}</span> : null}
      </span>
    </label>
  );
}
