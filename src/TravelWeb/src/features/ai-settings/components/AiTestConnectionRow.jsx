import { Loader2 } from "lucide-react";
import { Button } from "../../../components/ui/button";

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
      <Button type="button" variant="outline" onClick={onProbar} disabled={testing} className="gap-2">
        {testing && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
        {testing ? "Probando…" : "Probar conexión"}
      </Button>
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
