/**
 * Lista de avance de las notas de crédito de una anulación multi-factura (ADR-042, 2026-07-01).
 * Muestra una fila por nota con su ícono de estado (✔ emitida / ⏳ emitiendo… / ✗ no salió) y,
 * cuando una falló, el motivo que devolvió AFIP debajo. Se usa tanto en el estado PROCESANDO
 * como en el estado "en revisión" de CancelarReservaInline.jsx (Estados 2 y 4 de la spec).
 */

import { etiquetaNotaCredito, estadoVisualNota } from "../lib/multiCreditNoteFlow";

export function NotasCreditoProgressList({ creditNotes }) {
    if (!Array.isArray(creditNotes) || creditNotes.length === 0) return null;

    return (
        <ul className="w-full space-y-1.5 text-left" data-testid="lista-avance-notas-credito">
            {creditNotes.map((nota, index) => {
                const { icono, texto } = estadoVisualNota(nota.status);
                // Colores por estado: verde = emitida, ámbar = todavía emitiendo, rojo = no salió.
                const colorTexto =
                    nota.status === "Succeeded"
                        ? "text-emerald-700 dark:text-emerald-400"
                        : nota.status === "Failed"
                        ? "text-rose-700 dark:text-rose-400"
                        : "text-amber-700 dark:text-amber-400";

                return (
                    <li key={`${nota.currency}-${index}`} data-testid={`nota-credito-fila-${index}`}>
                        <div className={`flex items-center gap-2 text-sm font-medium ${colorTexto}`}>
                            <span aria-hidden="true">{icono}</span>
                            <span>{etiquetaNotaCredito(nota.currency)}</span>
                            <span className="text-xs font-normal opacity-80">— {texto}</span>
                        </div>
                        {/* El motivo de AFIP SÍ se muestra tal cual (info útil para el vendedor,
                            ya aprobado en H2) — nunca un texto crudo interno del backend. */}
                        {nota.status === "Failed" && nota.arcaErrorMessage && (
                            <p
                                className="ml-6 mt-0.5 text-xs text-rose-600 dark:text-rose-400"
                                data-testid={`nota-credito-motivo-${index}`}
                            >
                                Motivo de AFIP: «{nota.arcaErrorMessage}»
                            </p>
                        )}
                    </li>
                );
            })}
        </ul>
    );
}
