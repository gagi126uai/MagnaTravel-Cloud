import * as React from "react";
import { cn } from "../../lib/utils";
import { ListEmptyState } from "./ListEmptyState";

const DataGridContext = React.createContext({ density: "comfortable" });

const densityStyles = {
  comfortable: {
    header: "px-4 py-3",
    cell: "px-4 py-4",
    action: "px-4 py-4",
  },
  compact: {
    header: "px-4 py-2.5",
    cell: "px-4 py-3",
    action: "px-4 py-3",
  },
};

function useDataGridDensity() {
  const context = React.useContext(DataGridContext);
  return densityStyles[context.density] || densityStyles.comfortable;
}

function getAlignmentClass(align) {
  switch (align) {
    case "center":
      return "text-center";
    case "right":
      return "text-right";
    default:
      return "text-left";
  }
}

export function DataGrid({
  density = "comfortable",
  minWidth,
  className,
  tableClassName,
  responsive = true,
  children,
}) {
  return (
    <DataGridContext.Provider value={{ density }}>
      <div
        className={cn(
          "overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900",
          responsive && "hidden md:block",
          className
        )}
      >
        <div className="overflow-x-auto">
          <table
            className={cn("w-full border-collapse text-left text-sm", tableClassName)}
            style={minWidth ? { minWidth } : undefined}
          >
            {children}
          </table>
        </div>
      </div>
    </DataGridContext.Provider>
  );
}

export const DataGridHeader = React.forwardRef(({ className, ...props }, ref) => (
  <thead
    ref={ref}
    className={cn("bg-slate-50/60 text-slate-500 dark:bg-slate-950/70 dark:text-slate-400", className)}
    {...props}
  />
));
DataGridHeader.displayName = "DataGridHeader";

export const DataGridHeaderRow = React.forwardRef(({ className, ...props }, ref) => (
  <tr ref={ref} className={cn("border-b border-slate-200 dark:border-slate-800", className)} {...props} />
));
DataGridHeaderRow.displayName = "DataGridHeaderRow";

export const DataGridHeaderCell = React.forwardRef(({ align = "left", className, ...props }, ref) => {
  const density = useDataGridDensity();

  return (
    <th
      ref={ref}
      className={cn(
        density.header,
        getAlignmentClass(align),
        "text-[11px] font-semibold uppercase tracking-[0.14em] text-slate-500 dark:text-slate-400",
        className
      )}
      {...props}
    />
  );
});
DataGridHeaderCell.displayName = "DataGridHeaderCell";

export const DataGridBody = React.forwardRef(({ className, ...props }, ref) => (
  <tbody ref={ref} className={cn("divide-y divide-slate-100 dark:divide-slate-800", className)} {...props} />
));
DataGridBody.displayName = "DataGridBody";

export const DataGridRow = React.forwardRef(
  ({ className, clickable = false, interactive = true, inactive = false, ...props }, ref) => (
    <tr
      ref={ref}
      className={cn(
        interactive && "transition-colors hover:bg-slate-50/70 dark:hover:bg-slate-800/30",
        clickable && "cursor-pointer",
        inactive && "bg-slate-50/30 opacity-70 dark:bg-slate-900/30",
        className
      )}
      {...props}
    />
  )
);
DataGridRow.displayName = "DataGridRow";

export const DataGridCell = React.forwardRef(({ align = "left", className, ...props }, ref) => {
  const density = useDataGridDensity();

  return (
    <td
      ref={ref}
      className={cn(density.cell, getAlignmentClass(align), "align-middle text-slate-600 dark:text-slate-300", className)}
      {...props}
    />
  );
});
DataGridCell.displayName = "DataGridCell";

export const DataGridActionCell = React.forwardRef(({ align = "right", className, children, ...props }, ref) => {
  const density = useDataGridDensity();
  const justifyClass =
    align === "center" ? "justify-center" : align === "left" ? "justify-start" : "justify-end";

  return (
    <td
      ref={ref}
      className={cn(density.action, getAlignmentClass(align), "align-middle whitespace-nowrap", className)}
      {...props}
    >
      <div className={cn("flex items-center gap-1", justifyClass)}>{children}</div>
    </td>
  );
});
DataGridActionCell.displayName = "DataGridActionCell";

export function DataGridEmptyState({
  colSpan,
  icon,
  title,
  description,
  action,
  className,
}) {
  return (
    <tr>
      <td colSpan={colSpan} className={cn("p-0", className)}>
        <ListEmptyState icon={icon} title={title} description={description} action={action} />
      </td>
    </tr>
  );
}
