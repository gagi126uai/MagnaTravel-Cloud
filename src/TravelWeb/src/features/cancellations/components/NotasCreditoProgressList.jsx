/**
 * Lista de avance de las notas de crédito de una anulación multi-factura (ADR-042, 2026-07-01).
 * Muestra una fila por nota con su ícono de estado (✔ emitida / ⏳ emitiendo… / ✗ no salió) y,
 * cuando una falló, el motivo que devolvió AFIP debajo. Se usa tanto en el estado PROCESANDO
 * como en el estado "en revisión" de CancelarReservaInline.jsx (Estados 2 y 4 de la spec).
 */

import { etiquetaNotaCredito, estadoVisualNota, describirNotaPorFactura } from "../lib/multiCreditNoteFlow";

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

/**
 * Lista POR FACTURA de la franja "en revisión" de ReservaDetailPage (Rama A, alarma —
 * bloque 4, 2026-08-19). Distinta de `NotasCreditoProgressList` de arriba: esa agrupa por
 * MONEDA (sirve para el panel de anulación en curso, donde cada moneda tiene una sola
 * factura); esta agrupa por FACTURA, porque una reserva puede tener 2 facturas vivas en la
 * MISMA moneda y ahí "Nota de crédito en $" no alcanza para distinguir cuál es cuál.
 *
 * Usa `factura.comprobanteLabel` (ej. "Factura B 0001-00012345") en vez del símbolo de
 * moneda — mismo copy exacto de la spec.
 */
export function NotasCreditoPorFacturaList({ creditNotes }) {
    if (!Array.isArray(creditNotes) || creditNotes.length === 0) return null;

    return (
        <ul className="w-full space-y-1 text-left" data-testid="lista-notas-por-factura">
            {creditNotes.map((nota, index) => {
                const { icono, texto } = describirNotaPorFactura(nota);
                const colorTexto =
                    nota.status === "Succeeded"
                        ? "text-emerald-700 dark:text-emerald-400"
                        : "text-rose-700 dark:text-rose-400"; // Failed y Pending-atascada comparten color: las dos necesitan acción.

                return (
                    <li
                        key={nota.originatingInvoicePublicId ?? index}
                        data-testid={`nota-por-factura-fila-${index}`}
                        className={`text-sm font-medium ${colorTexto}`}
                    >
                        <span aria-hidden="true">{icono}</span> {texto}
                    </li>
                );
            })}
        </ul>
    );
}
