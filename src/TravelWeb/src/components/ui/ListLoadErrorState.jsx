import { RotateCcw } from "lucide-react";
import { cn } from "../../lib/utils";

/**
 * Cartel de error para cuando falla la carga de un listado (red caída, error del
 * servidor, timeout, etc). Mismo patrón visual que ya usa la solapa "Copias de
 * seguridad" (fila roja + botón "Probar de nuevo"): un error nunca debe dejar al
 * usuario sin una salida (P-11⭐ de la constitución del producto).
 *
 * No confundir con `DatabaseUnavailableState`: ese es para cuando la BASE DE
 * DATOS está caída (un problema de infraestructura, sin retry con sentido);
 * este cartel es para cualquier OTRO error al pedir datos, donde reintentar sí
 * puede funcionar.
 */
export function ListLoadErrorState({ message, onRetry, className }) {
  return (
    <div
      role="alert"
      className={cn(
        "flex flex-col items-center justify-between gap-3 rounded-xl border border-rose-200 bg-rose-50 px-6 py-6 text-center text-sm text-rose-800 dark:border-rose-900/40 dark:bg-rose-950/20 dark:text-rose-200 sm:flex-row sm:text-left",
        className
      )}
    >
      <span>{message}</span>
      <button
        type="button"
        onClick={onRetry}
        data-testid="list-load-error-retry"
        className="inline-flex shrink-0 items-center gap-1.5 rounded-lg border border-rose-300 px-3 py-1.5 text-xs font-bold text-rose-700 hover:bg-rose-50 dark:border-rose-800 dark:text-rose-300 dark:hover:bg-rose-950/30"
      >
        <RotateCcw className="h-3.5 w-3.5" />
        Probar de nuevo
      </button>
    </div>
  );
}
