import { cn } from "../../lib/utils";

export function ListToolbar({
  searchSlot,
  filterSlot,
  actionSlot,
  className,
  leadClassName,
  actionClassName,
}) {
  const hasLead = Boolean(searchSlot) || Boolean(filterSlot);

  return (
    <div
      className={cn(
        "rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50",
        className
      )}
    >
      <div className="flex flex-wrap items-center justify-between gap-4">
        {searchSlot && (
          <div className={cn("min-w-0 flex-1", leadClassName)}>
            {searchSlot}
          </div>
        )}
        
        <div className="flex flex-wrap items-center gap-3">
          {filterSlot && (
            <div className="flex items-center gap-2">
              {filterSlot}
            </div>
          )}
          {actionSlot && (
            <div className={cn("flex items-center gap-2", actionClassName)}>
              {actionSlot}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
