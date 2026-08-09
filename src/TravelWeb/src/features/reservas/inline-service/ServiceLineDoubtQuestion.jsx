/**
 * La "duda grande" de la línea inteligente (spec firmada 2026-08-07, §4): una única línea
 * con la pregunta que armó el motor + dos botones Sí/No. Va DEBAJO del campo al que se
 * refiere (lo decide el *InlineForm que la usa, según `doubt.field`) — nunca una ventana
 * flotante (regla del Cartel Emergente 2026-07-22, que excluye las fichas de trabajo).
 *
 * "Sí" cierra la línea sin tocar nada (el amarillo queda como está). "No" vacía el campo
 * y le deja el foco al vendedor. Guardar funciona igual con la duda abierta: nunca traba
 * nada (es "ignorable" por diseño).
 */
export function ServiceLineDoubtQuestion({ doubt, onRespuesta }) {
  if (!doubt) return null;
  return (
    <div
      className="flex flex-wrap items-center gap-2 text-xs text-amber-800 bg-amber-50 border border-amber-200 rounded-lg px-3 py-2 mt-1"
      data-testid="service-line-doubt"
    >
      {/* role="status" SOLO en el texto de la pregunta (lo que un lector de pantalla debe
          anunciar solo). Antes envolvía también los botones — Sí/No no son "un estado
          que se anuncia", son controles interactivos; mezclarlos ahí confundía a un
          lector de pantalla (hallazgo del reviewer de accesibilidad). */}
      <span className="font-medium" role="status">{doubt.question}</span>
      <div className="flex gap-1.5 ml-auto">
        <button
          type="button"
          onClick={() => onRespuesta(true)}
          className="px-2.5 py-1 rounded-md text-xs font-semibold bg-white border border-amber-300 text-amber-800 hover:bg-amber-100 transition-colors"
          data-testid="service-line-doubt-si"
          aria-label={`Sí — ${doubt.question}`}
        >
          Sí
        </button>
        <button
          type="button"
          onClick={() => onRespuesta(false)}
          className="px-2.5 py-1 rounded-md text-xs font-semibold bg-white border border-amber-300 text-amber-800 hover:bg-amber-100 transition-colors"
          data-testid="service-line-doubt-no"
          aria-label={`No — ${doubt.question}`}
        >
          No
        </button>
      </div>
    </div>
  );
}
