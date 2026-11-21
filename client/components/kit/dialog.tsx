import { useEffect, useId, useRef, type ReactNode } from "react";
import { X } from "lucide-react-native";
import { useTheme } from "@/components/theme/theme";
import { useT } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import { IconButton } from "./button";

export type DialogProps = {
  title: string;
  onClose: () => void;
  children: ReactNode;
  /** The buttons at the bottom: the main action last. */
  footer?: ReactNode;
  wide?: boolean;
};

/**
 * A dialog over the page, on the browser's own <dialog>: it traps focus, closes on Escape, and returns focus
 * to whatever opened it. It is mounted while it is open and unmounted to close.
 */
export function Dialog({ title, onClose, children, footer, wide }: DialogProps) {
  const t = useT("common");
  const { colors } = useTheme();
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();

  useEffect(() => {
    const dialog = ref.current;
    if (dialog && !dialog.open) {
      dialog.showModal();
      // The browser puts focus on the first control, which is Close. A dialog is opened to fill something in,
      // so its first field takes it; a dialog with no field leaves focus where the browser put it.
      dialog.querySelector<HTMLElement>("textarea, select, input:not([type=hidden]):not([readonly])")?.focus();
    }
    return () => dialog?.close();
  }, []);

  return (
    <dialog
      ref={ref}
      aria-labelledby={titleId}
      onCancel={(event) => {
        event.preventDefault();
        onClose();
      }}
      onClick={(event) => {
        // A click on the backdrop lands on the dialog itself.
        if (event.target === ref.current) onClose();
      }}
      className={cn("m-auto w-[calc(100vw-32px)] rounded-xl border border-border bg-card p-0 text-foreground shadow-xl backdrop:bg-black/50", wide ? "max-w-[720px]" : "max-w-[480px]")}
    >
      <div className="flex max-h-[calc(100vh-48px)] flex-col">
        <header className="flex items-center justify-between gap-4 border-b border-border px-5 py-3">
          <h2 id={titleId} className="text-base font-semibold">
            {title}
          </h2>
          <IconButton label={t("actions.close")} icon={<X size={18} color={colors.mutedForeground} />} onClick={onClose} />
        </header>
        <div className="flex min-h-0 flex-col gap-4 overflow-y-auto px-5 py-4">{children}</div>
        {footer ? <footer className="flex flex-wrap items-center justify-end gap-2 border-t border-border px-5 py-3">{footer}</footer> : null}
      </div>
    </dialog>
  );
}
