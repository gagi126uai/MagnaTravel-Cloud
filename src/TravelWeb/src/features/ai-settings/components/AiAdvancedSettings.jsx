import { ChevronDown, ChevronRight } from "lucide-react";

/**
 * "Ajustes avanzados" (§15.6): Dirección y Modelo, plegados por defecto (criterio "Más
 * detalles cerrado", 2026-06-06 Ronda 7). Se abren solos y quedan obligatorios cuando el
 * proveedor elegido es "Otra" (`forced`) — ahí no hay valores recomendados que precargar.
 *
 * Fix reviewer (hallazgo menor 2): con "Otra", Dirección/Modelo vacíos al guardar ahora
 * muestran un error CORTO pegado al campo (`baseUrlError`/`modelError`), en vez de caer
 * únicamente al cartel rojo general de arriba.
 */
export function AiAdvancedSettings({
  open,
  forced,
  onToggle,
  baseUrl,
  model,
  onChangeBaseUrl,
  onChangeModel,
  onVolverARecomendados,
  puedeVolverARecomendados,
  baseUrlError,
  modelError,
}) {
  const estaAbierto = open || forced;

  return (
    <div className="border-t border-slate-100 dark:border-slate-800 pt-4">
      <button
        type="button"
        onClick={onToggle}
        disabled={forced}
        className="flex items-center gap-1.5 text-sm font-semibold text-slate-600 dark:text-slate-300 disabled:opacity-70 disabled:cursor-not-allowed"
      >
        {estaAbierto ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
        Ajustes avanzados
      </button>

      {estaAbierto && (
        <div className="mt-3 space-y-3 pl-1">
          <div>
            <label htmlFor="ai-base-url" className="block text-xs font-medium text-slate-600 dark:text-slate-400 mb-1">
              Dirección
            </label>
            <input
              id="ai-base-url"
              type="text"
              value={baseUrl}
              onChange={(event) => onChangeBaseUrl(event.target.value)}
              className="w-full h-9 rounded-md border border-slate-300 bg-white px-3 text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent dark:bg-slate-950 dark:border-slate-800 dark:text-slate-50"
            />
            {baseUrlError && (
              <p role="alert" className="mt-1 text-xs font-medium text-rose-600 dark:text-rose-400">
                {baseUrlError}
              </p>
            )}
          </div>
          <div>
            <label htmlFor="ai-model" className="block text-xs font-medium text-slate-600 dark:text-slate-400 mb-1">
              Modelo
            </label>
            <input
              id="ai-model"
              type="text"
              value={model}
              onChange={(event) => onChangeModel(event.target.value)}
              className="w-full h-9 rounded-md border border-slate-300 bg-white px-3 text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent dark:bg-slate-950 dark:border-slate-800 dark:text-slate-50"
            />
            {modelError && (
              <p role="alert" className="mt-1 text-xs font-medium text-rose-600 dark:text-rose-400">
                {modelError}
              </p>
            )}
          </div>
          {puedeVolverARecomendados && (
            <button
              type="button"
              onClick={onVolverARecomendados}
              className="text-sm font-semibold text-primary hover:text-primary/80"
            >
              Volver a los valores recomendados
            </button>
          )}
        </div>
      )}
    </div>
  );
}
