/**
 * Etiqueta de estado de pago al operador para UN servicio de la reserva.
 *
 * Guía UX 2026-06-21, P4=B:
 *   - "paid"    → "✔ Operador pagado"          (verde)
 *   - "partial" → "⚠ Pago parcial al operador" (ámbar)
 *   - "unpaid"  → "⚠ Operador impago"          (ámbar)
 *
 * Regla de costos:
 *   El ESTADO lo ven todos.
 *   Los MONTOS (costo, pagado, saldo) solo si amountsVisible === true en el DTO raíz.
 *   El backend ya aplica el enmascarado — acá solo leemos el flag.
 *
 * Este componente NO tiene acción propia (la guía dice: "es estado, no acción").
 * El registro de pago se hace desde la ficha del proveedor.
 *
 * Props:
 *   servicioStatus — objeto ServiceSupplierPaymentStatusDto del backend para ESTE servicio,
 *                    o null si aún no cargó / no se encontró.
 *   amountsVisible — bool, viene del DTO raíz (ReservaSupplierPaymentStatusDto.amountsVisible).
 *   loading        — bool, true mientras el hook está cargando.
 */

import { formatCurrency } from "../../../lib/utils";

export function OperadorPagoStatusBadge({ servicioStatus, amountsVisible, loading }) {
    // Mientras carga, no mostramos nada para no hacer parpadear la UI.
    // El resto de la fila sigue renderizando sin esperar este dato.
    if (loading) return null;

    // Si no hay dato (endpoint falló o servicio sin proveedor), no mostramos nada.
    // Degradación silenciosa: la solapa no se rompe.
    if (!servicioStatus) return null;

    const { status, netCost, paidToOperator, outstandingToOperator, currency } = servicioStatus;

    // Decidir color y texto según el status
    if (status === "paid") {
        return (
            <span
                className="inline-flex items-center gap-1 text-[10px] font-semibold text-emerald-700 dark:text-emerald-400"
                data-testid="badge-operador-pagado"
                title="Este servicio está completamente pagado al operador"
            >
                {/* Checkmark simple sin ícono importado para mantener el componente liviano */}
                <span className="h-2 w-2 rounded-full bg-emerald-500 flex-shrink-0" aria-hidden="true" />
                Operador pagado
                {/* Solo mostramos monto si el usuario tiene permiso de costos (amountsVisible del backend) */}
                {amountsVisible && netCost > 0 && (
                    <span className="font-mono opacity-75">
                        ({formatCurrency(paidToOperator, currency || "ARS")})
                    </span>
                )}
            </span>
        );
    }

    if (status === "partial") {
        return (
            <span
                className="inline-flex items-center gap-1 text-[10px] font-semibold text-amber-700 dark:text-amber-400"
                data-testid="badge-operador-parcial"
                title={
                    amountsVisible
                        ? `Pagado: ${formatCurrency(paidToOperator, currency || "ARS")} · Saldo: ${formatCurrency(outstandingToOperator, currency || "ARS")}`
                        : "El operador tiene un pago parcial — falta completar"
                }
            >
                <span className="h-2 w-2 rounded-full bg-amber-400 flex-shrink-0" aria-hidden="true" />
                Pago parcial al operador
                {amountsVisible && outstandingToOperator > 0 && (
                    <span className="font-mono opacity-75">
                        (resta {formatCurrency(outstandingToOperator, currency || "ARS")})
                    </span>
                )}
            </span>
        );
    }

    if (status === "unpaid") {
        return (
            <span
                className="inline-flex items-center gap-1 text-[10px] font-semibold text-amber-700 dark:text-amber-400"
                data-testid="badge-operador-impago"
                title={
                    amountsVisible && netCost > 0
                        ? `Deuda con el operador: ${formatCurrency(netCost, currency || "ARS")}`
                        : "El operador todavía no tiene ningún pago registrado para este servicio"
                }
            >
                <span className="h-2 w-2 rounded-full bg-amber-400 flex-shrink-0" aria-hidden="true" />
                Operador impago
                {/* Mostramos el costo como referencia del total a pagar, solo con permiso */}
                {amountsVisible && netCost > 0 && (
                    <span className="font-mono opacity-75">
                        ({formatCurrency(netCost, currency || "ARS")})
                    </span>
                )}
            </span>
        );
    }

    // Status desconocido: no mostramos nada (degradación silenciosa)
    return null;
}
