import { cn } from "../../lib/utils";

export function Skeleton({ className, ...props }) {
    return (
        <div
            className={cn(
                "animate-pulse rounded-lg bg-slate-200/80 dark:bg-slate-800/80",
                className
            )}
            {...props}
        />
    );
}

// ─── Pre-built skeleton patterns ───────────────────────────

export function SkeletonKpiCard() {
    return (
        <div className="rounded-xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900/50 p-5 space-y-3">
            <div className="flex items-center justify-between">
                <Skeleton className="h-3 w-20" />
                <Skeleton className="h-4 w-4 rounded-full" />
            </div>
            <Skeleton className="h-8 w-28" />
            <Skeleton className="h-3 w-16" />
        </div>
    );
}

export function SkeletonKpiRow({ count = 4 }) {
    return (
        <div className={`grid gap-4 grid-cols-2 md:grid-cols-${count}`}>
            {Array.from({ length: count }).map((_, i) => (
                <SkeletonKpiCard key={i} />
            ))}
        </div>
    );
}

export function SkeletonTableRow({ cols = 5 }) {
    return (
        <div className="flex items-center gap-4 px-6 py-4 border-b border-slate-100 dark:border-slate-800 last:border-0">
            <Skeleton className="h-9 w-9 rounded-full shrink-0" />
            <div className="flex-1 space-y-2">
                <Skeleton className="h-4 w-32" />
                <Skeleton className="h-3 w-20" />
            </div>
            {Array.from({ length: cols - 1 }).map((_, i) => (
                <Skeleton key={i} className="h-4 w-16 hidden sm:block" />
            ))}
        </div>
    );
}

export function SkeletonTable({ rows = 5, cols = 5 }) {
    return (
        <div className="rounded-xl border border-slate-200 bg-white dark:bg-slate-900 dark:border-slate-800 overflow-hidden">
            {/* Header */}
            <div className="flex items-center gap-4 px-6 py-3 bg-slate-50 dark:bg-slate-950 border-b border-slate-200 dark:border-slate-800">
                {Array.from({ length: cols }).map((_, i) => (
                    <Skeleton key={i} className="h-3 w-16" />
                ))}
            </div>
            {/* Rows */}
            {Array.from({ length: rows }).map((_, i) => (
                <SkeletonTableRow key={i} cols={cols} />
            ))}
        </div>
    );
}

export function SkeletonChart() {
    return (
        <div className="rounded-xl border border-slate-200 bg-white dark:bg-slate-900 dark:border-slate-800 p-6 space-y-4">
            <div className="space-y-2">
                <Skeleton className="h-5 w-40" />
                <Skeleton className="h-3 w-60" />
            </div>
            <div className="flex items-end gap-3 h-[250px] pt-4">
                {[40, 65, 55, 80, 45, 70, 60, 75, 50, 85, 55, 90].map((h, i) => (
                    <Skeleton
                        key={i}
                        className="flex-1 rounded-t-md"
                        style={{ height: `${h}%` }}
                    />
                ))}
            </div>
        </div>
    );
}

// ─── Full page skeletons ───────────────────────────────────

export function DashboardSkeleton() {
    return (
        <div className="space-y-8 animate-in fade-in duration-300">
            <div className="space-y-2">
                <Skeleton className="h-8 w-48" />
                <Skeleton className="h-4 w-72" />
            </div>
            <SkeletonKpiRow count={4} />
            <div className="grid gap-6 lg:grid-cols-5">
                <div className="lg:col-span-3">
                    <SkeletonChart />
                </div>
                <div className="lg:col-span-2">
                    <SkeletonChart />
                </div>
            </div>
            <div className="grid gap-6 lg:grid-cols-2">
                <SkeletonTable rows={5} cols={4} />
                <SkeletonTable rows={5} cols={3} />
            </div>
        </div>
    );
}

export function FilesPageSkeleton() {
    return (
        <div className="space-y-4 md:space-y-6 animate-in fade-in duration-300">
            <div className="flex justify-between items-center">
                <div className="space-y-2">
                    <Skeleton className="h-7 w-52" />
                    <Skeleton className="h-4 w-80" />
                </div>
                <Skeleton className="h-10 w-44 rounded-lg" />
            </div>
            <SkeletonKpiRow count={4} />
            {/* Toolbar */}
            <div className="flex items-center justify-between p-3 rounded-xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900/50">
                <div className="flex gap-2">
                    {[1, 2, 3, 4, 5].map((i) => (
                        <Skeleton key={i} className="h-8 w-20 rounded-lg" />
                    ))}
                </div>
                <Skeleton className="h-9 w-56 rounded-lg" />
            </div>
            <SkeletonTable rows={8} cols={6} />
        </div>
    );
}

export function ReportsSkeleton() {
    return (
        <div className="space-y-6 animate-in fade-in duration-300">
            <div className="space-y-2">
                <Skeleton className="h-8 w-36" />
                <Skeleton className="h-4 w-64" />
            </div>
            {/* Date bar */}
            <div className="flex items-center justify-between p-4 rounded-xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900/50">
                <div className="flex gap-2">
                    {[1, 2, 3, 4, 5].map((i) => (
                        <Skeleton key={i} className="h-7 w-20 rounded-lg" />
                    ))}
                </div>
                <div className="flex gap-2">
                    <Skeleton className="h-7 w-32 rounded-lg" />
                    <Skeleton className="h-7 w-32 rounded-lg" />
                </div>
            </div>
            <SkeletonKpiRow count={4} />
            <SkeletonChart />
            <div className="grid gap-6 lg:grid-cols-2">
                <SkeletonTable rows={5} cols={3} />
                <SkeletonTable rows={5} cols={3} />
            </div>
        </div>
    );
}

export function AccountPageSkeleton() {
    return (
        <div className="space-y-6 animate-in fade-in duration-300">
            {/* Header */}
            <div className="flex items-center gap-4">
                <Skeleton className="h-10 w-10 rounded-lg" />
                <div className="space-y-2">
                    <Skeleton className="h-7 w-48" />
                    <Skeleton className="h-4 w-32" />
                </div>
            </div>
            {/* Info card */}
            <div className="rounded-xl border border-slate-200 dark:border-slate-800 p-6 space-y-4">
                <div className="flex items-center gap-4">
                    <Skeleton className="h-16 w-16 rounded-full" />
                    <div className="space-y-2 flex-1">
                        <Skeleton className="h-6 w-40" />
                        <div className="flex gap-4">
                            <Skeleton className="h-4 w-32" />
                            <Skeleton className="h-4 w-28" />
                        </div>
                    </div>
                </div>
            </div>
            {/* Summary */}
            <SkeletonKpiRow count={4} />
            {/* Tables */}
            <SkeletonTable rows={4} cols={5} />
            <SkeletonTable rows={4} cols={3} />
        </div>
    );
}

