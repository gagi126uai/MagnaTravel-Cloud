import { cn } from "../../lib/utils";

export function MobileRecordList({ children, className, responsive = true }) {
  return <div className={cn("space-y-3", responsive && "md:hidden", className)}>{children}</div>;
}

export function MobileRecordCard({
  accentSlot,
  statusSlot,
  title,
  subtitle,
  meta,
  footer,
  footerActions,
  className,
  inactive = false,
  onClick,
}) {
  return (
    <div
      onClick={onClick}
      className={cn(
        "rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900",
        onClick && "cursor-pointer transition-transform active:scale-[0.99]",
        inactive && "opacity-70",
        className
      )}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="flex min-w-0 items-start gap-3">
          {accentSlot ? <div className="shrink-0">{accentSlot}</div> : null}
          <div className="min-w-0">
            <div className="truncate text-sm font-semibold text-slate-900 dark:text-white">{title}</div>
            {subtitle ? (
              <div className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{subtitle}</div>
            ) : null}
          </div>
        </div>
        {statusSlot ? <div className="shrink-0">{statusSlot}</div> : null}
      </div>

      {meta ? <div className="mt-3 grid gap-2 text-sm text-slate-600 dark:text-slate-400">{meta}</div> : null}

      {footer || footerActions ? (
        <div className="mt-3 flex items-center justify-between gap-3 border-t border-slate-100 pt-3 dark:border-slate-800">
          <div className="min-w-0">{footer}</div>
          {footerActions ? <div className="flex shrink-0 items-center gap-1">{footerActions}</div> : null}
        </div>
      ) : null}
    </div>
  );
}
