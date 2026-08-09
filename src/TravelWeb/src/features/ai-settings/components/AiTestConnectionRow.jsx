import { Loader2 } from "lucide-react";

/**
 * Botón "Probar conexión" + el resultado en la misma línea (§15.4). Probar NO guarda nada:
 * es solo un saludo mínimo al proveedor con lo que hay en pantalla en ese momento.
 *
 * `resultado` es `{ texto, esExito }` (fix reviewer, hallazgo menor 3): antes el color se
 * elegía adivinando con `texto.startsWith("Funciona")`, algo frágil ante cualquier cambio
 * de redacción. Ahora el booleano lo decide `construirResultadoPrueba` (la única fuente
 * de la frase), y este componente solo lo lee.
 */
export function AiTestConnectionRow({ testing, resultado, onProbar }) {
  return (
    <div className="flex flex-wrap items-center gap-3">
      <button
        type="button"
        onClick={onProbar}
        disabled={testing}
        className="inline-flex items-center gap-2 rounded-xl border border-slate-300 dark:border-slate-700 px-4 py-2 text-sm font-semibold text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800 disabled:opacity-50"
      >
        {testing && <Loader2 className="h-4 w-4 animate-spin" />}
        {testing ? "Probando…" : "Probar conexión"}
      </button>
      {resultado && (
        <span
          role="status"
          className={`text-sm font-medium ${resultado.esExito ? "text-emerald-600 dark:text-emerald-400" : "text-rose-600 dark:text-rose-400"}`}
        >
          {resultado.texto}
        </span>
      )}
    </div>
  );
}
