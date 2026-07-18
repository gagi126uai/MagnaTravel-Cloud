/**
 * Etiqueta chica en la fila de un servicio ANULADO que dejó multa del operador.
 *
 * Spec UX T4 (docs/ux/2026-07-17-t4-estados-derivados-ficha-reserva.md, Punto 4,
 * P3 FIRMADA por Gastón): reusa el MISMO formato visual que el badge hermano
 * OperadorPagoStatusBadge (puntito + texto chico, sin recuadro), pero es SOLO un
 * cartelito de aviso — nunca muestra montos ni tiene acción propia. La multa se
 * resuelve/mira en su lugar único (paso de multa en la ficha + chip Pago + extracto).
 *
 * Valores de `cancellationPenaltyState` (campo que manda el backend por servicio,
 * ver HotelBookingDto.CancellationPenaltyState y hermanos):
 *   - "Pending"   → "Con multa" ámbar (todavía no se cobró del todo).
 *   - "Collected" → "✓ Multa cobrada" gris (se cobró por completo; NO desaparece,
 *                    queda a la vista en gris — mismo criterio que el cartel de
 *                    multa cobrada de la ficha, 2026-07-16).
 *   - null/undefined/cualquier otro valor → no se muestra nada (degradación
 *     silenciosa: el servicio anulado no tiene multa, o el backend no pudo
 *     calcularla — nunca se inventa un estado).
 */
export function CancellationPenaltyLabel({ cancellationPenaltyState }) {
    if (cancellationPenaltyState === "Pending") {
        return (
            <span
                className="inline-flex items-center gap-1 text-[10px] font-semibold text-amber-700 dark:text-amber-400"
                data-testid="label-servicio-con-multa"
                title="Este servicio anulado dejó una multa del operador que todavía no se cobró del todo."
            >
                <span className="h-2 w-2 rounded-full bg-amber-400 flex-shrink-0" aria-hidden="true" />
                Con multa
            </span>
        );
    }

    if (cancellationPenaltyState === "Collected") {
        return (
            <span
                className="inline-flex items-center gap-1 text-[10px] font-semibold text-slate-500 dark:text-slate-400"
                data-testid="label-servicio-multa-cobrada"
                title="La multa de este servicio anulado ya se cobró por completo."
            >
                <span className="h-2 w-2 rounded-full bg-slate-400 flex-shrink-0" aria-hidden="true" />
                ✓ Multa cobrada
            </span>
        );
    }

    return null;
}
