import { Inbox } from "lucide-react";
import { cn } from "../../lib/utils";

export function ListEmptyState({
  icon: Icon = Inbox,
  title = "Sin resultados",
  description,
  action,
  compact = false,
  className,
}) {
  return (
    <div
      className={cn(
        "flex flex-col items-center justify-center text-center",
        compact ? "px-4 py-8" : "px-6 py-12",
        className
      )}
    >
      <div className="mb-3 flex h-12 w-12 items-center justify-center rounded-full border border-slate-100 bg-white text-slate-300 shadow-sm dark:border-slate-800 dark:bg-slate-900 dark:text-slate-600">
        <Icon className="h-5 w-5" />
      </div>
      <p className="text-sm font-medium text-slate-700 dark:text-slate-200">{title}</p>
      {description ? (
        <p className="mt-1 max-w-md text-xs text-slate-500 dark:text-slate-400">{description}</p>
      ) : null}
      {action ? <div className="mt-4">{action}</div> : null}
    </div>
  );
}
