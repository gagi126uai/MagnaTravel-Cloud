import { cn } from "../../lib/utils";

export function ListPageHeader({
  title,
  subtitle,
  actions,
  className,
  titleClassName,
  subtitleClassName,
}) {
  return (
    <div className={cn("flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between", className)}>
      <div className="min-w-0 space-y-1">
        <h1 className={cn("text-xl font-bold tracking-tight text-slate-900 dark:text-white md:text-2xl", titleClassName)}>
          {title}
        </h1>
        {subtitle ? (
          <p className={cn("text-sm text-slate-500 dark:text-slate-400", subtitleClassName)}>{subtitle}</p>
        ) : null}
      </div>
      {actions ? <div className="flex flex-wrap items-center gap-2">{actions}</div> : null}
    </div>
  );
}
