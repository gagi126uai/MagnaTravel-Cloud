import { construirFotoEstado } from "../lib/aiSettingsPresentation.js";

/**
 * La "foto" de una sola linea de arriba de la pantalla (§15.5 de la spec firmada): dice si
 * la inteligencia artificial esta funcionando, sin configurar, o configurada pero con la
 * ultima prueba fallida. Cero palabras tecnicas, nunca el codigo interno.
 */
export function AiStatusBanner({ statusCode, providerDisplayName, providerCode }) {
  const { emoji, texto } = construirFotoEstado({ statusCode, providerDisplayName, providerCode });

  return (
    <div
      role="status"
      className="rounded-[14px] border border-slate-200 dark:border-slate-800 bg-slate-50/60 dark:bg-slate-800/20 px-5 py-3.5 flex items-center gap-3"
    >
      <span className="text-xl leading-none" aria-hidden="true">{emoji}</span>
      <span className="text-sm font-semibold text-slate-800 dark:text-slate-100">{texto}</span>
    </div>
  );
}
