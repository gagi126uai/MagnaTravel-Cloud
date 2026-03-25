import { DatabaseZap } from "lucide-react";

export function DatabaseUnavailableState({ message = "La base de datos no esta disponible en este momento." }) {
  return (
    <div className="rounded-3xl border border-amber-200 bg-amber-50 px-6 py-10 text-center shadow-sm dark:border-amber-900/40 dark:bg-amber-950/20">
      <div className="mx-auto mb-4 flex h-14 w-14 items-center justify-center rounded-full bg-white text-amber-600 shadow-sm dark:bg-slate-900 dark:text-amber-300">
        <DatabaseZap className="h-6 w-6" />
      </div>
      <div className="text-lg font-semibold text-slate-900 dark:text-white">Base de datos no disponible</div>
      <div className="mt-2 text-sm text-slate-600 dark:text-slate-300">{message}</div>
    </div>
  );
}
