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
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        {hasLead ? (
          <div className={cn("flex min-w-0 flex-1 flex-col gap-3 md:flex-row md:items-center", leadClassName)}>
            {searchSlot ? <div className="min-w-0 flex-1">{searchSlot}</div> : null}
            {filterSlot ? <div className="flex flex-wrap items-center gap-2">{filterSlot}</div> : null}
          </div>
        ) : (
          <div />
        )}
        {actionSlot ? <div className={cn("flex flex-wrap items-center gap-2", actionClassName)}>{actionSlot}</div> : null}
      </div>
    </div>
  );
}
