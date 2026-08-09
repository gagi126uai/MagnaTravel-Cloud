import { CheckCircle2 } from "lucide-react";
import { AI_API_KEY_FIELD_MODE, construirAyudaClave } from "../lib/aiSettingsPresentation.js";

/**
 * El campo "Clave", write-only (§15.3): se pega y nunca se vuelve a ver. Sin ojito, sin
 * botón de copiar — eso es prohibición explícita de la spec (§15.9), no un descuido.
 *
 * `modo` decide que se dibuja (ver aiSettingsPresentation.js):
 *  - vacia: nunca hubo clave PARA EL PROVEEDOR ELEGIDO (o el usuario cambió de proveedor
 *    respecto al que tiene la clave guardada), se ve un input para pegarla.
 *  - configurada: hay clave guardada por el dueño para ESTE proveedor, se ve
 *    "Configurada ✓ · empieza con XXXX…".
 *  - cambiando: el dueño apretó "Cambiar la clave", se ve un input para pegar la nueva.
 *  - respaldoServidor: la clave la dejó el técnico al instalar; se ve un input (para
 *    reemplazarla si quiere) con un aviso de que es un respaldo.
 */
export function AiApiKeyField({
  modo,
  providerDisplayName,
  providerCode,
  apiKeyPrefix,
  apiKeyInput,
  onChangeApiKeyInput,
  onCambiarClave,
  onCancelarCambio,
  fieldError,
}) {
  const ayuda = construirAyudaClave(modo, providerDisplayName, providerCode);

  // Fix reviewer (bloqueante B1, parte 2): el error de campo se renderiza SIEMPRE que
  // exista, sin importar el modo. Antes vivía solo en la rama de abajo (con input) — el
  // caso real que fallaba en silencio era "cambiaste de proveedor sin pegar clave nueva"
  // cayendo en la rama CONFIGURED (return temprano) mientras el modo no reflejaba el
  // cambio, y el mensaje del servidor no tenía dónde aparecer. Con el fix de
  // calcularModoCampoClave ese caso ya no cae en CONFIGURED, pero este render defensivo
  // se mantiene: cualquier error de campo que llegue tiene que verse, pase lo que pase.
  const bloqueError = fieldError && (
    <p role="alert" className="text-xs font-medium text-rose-600 dark:text-rose-400">
      {fieldError}
    </p>
  );

  if (modo === AI_API_KEY_FIELD_MODE.CONFIGURED) {
    return (
      <div className="space-y-1.5">
        <span className="block text-sm font-medium text-slate-700 dark:text-slate-300">Clave</span>
        <div className="flex flex-wrap items-center gap-3">
          <span className="inline-flex items-center gap-1.5 text-sm text-emerald-700 dark:text-emerald-400 font-medium">
            <CheckCircle2 className="h-4 w-4" />
            Configurada ✓ · empieza con {apiKeyPrefix || "····"}…
          </span>
          <button
            type="button"
            onClick={onCambiarClave}
            className="text-sm font-semibold text-indigo-600 hover:text-indigo-700 dark:text-indigo-400"
          >
            Cambiar la clave
          </button>
        </div>
        {bloqueError}
      </div>
    );
  }

  const mostrarCancelar = modo === AI_API_KEY_FIELD_MODE.CHANGING;

  return (
    <div className="space-y-1.5">
      <label htmlFor="ai-api-key-input" className="block text-sm font-medium text-slate-700 dark:text-slate-300">
        Clave
      </label>
      <div className="flex flex-wrap items-center gap-3">
        <input
          id="ai-api-key-input"
          type="password"
          autoComplete="new-password"
          value={apiKeyInput}
          onChange={(event) => onChangeApiKeyInput(event.target.value)}
          className="flex-1 min-w-[220px] h-10 rounded-md border border-slate-300 bg-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent dark:bg-slate-950 dark:border-slate-800 dark:text-slate-50"
        />
        {mostrarCancelar && (
          <button
            type="button"
            onClick={onCancelarCambio}
            className="text-sm font-semibold text-slate-500 hover:text-slate-700 dark:text-slate-400"
          >
            Cancelar el cambio
          </button>
        )}
      </div>
      <p className="text-xs text-slate-500 dark:text-slate-400">{ayuda}</p>
      {bloqueError}
    </div>
  );
}
