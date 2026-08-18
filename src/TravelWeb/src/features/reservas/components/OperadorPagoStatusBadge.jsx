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
 * Fix docblock (review frontend, 2026-08-04): esta nota quedó desactualizada y
 * contradecía al código de abajo. Texto correcto — regla firmada, Tanda 2 del
 * rediseño de fichas, 2026-08-03: NINGUNA de las tres etiquetas ("pagado",
 * "parcial", "impago") muestra el monto al lado del texto, tenga o no
 * amountsVisible=true — todas quedan igual de simples sin importar el permiso
 * de quien las mira. En "parcial" e "impago", el monto (con permiso) sigue
 * disponible al pasar el mouse (title). "Pagado" no lo necesita en el title:
 * no queda saldo pendiente que mostrar. El detalle completo, en cualquier
 * caso, vive en la solapa Estado de Cuenta / cuenta del proveedor.
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
                className="inline-flex items-center gap-1 text-[11px] font-semibold text-emerald-700 dark:text-emerald-400"
                data-testid="badge-operador-pagado"
                title="Este servicio está completamente pagado al operador"
            >
                {/* Checkmark simple sin ícono importado para mantener el componente liviano */}
                <span className="h-2 w-2 rounded-full bg-emerald-500 flex-shrink-0" aria-hidden="true" />
                Operador pagado
                {/* Sin monto (nota firmada de la maqueta, 2026-08-03): la etiqueta del operador
                    NUNCA lleva plata al lado, en ninguna variante — el número vive en el title
                    y en la cuenta del proveedor. */}
            </span>
        );
    }

    if (status === "partial") {
        return (
            <span
                className="inline-flex items-center gap-1 text-[11px] font-semibold text-amber-700 dark:text-amber-400"
                data-testid="badge-operador-parcial"
                title={
                    amountsVisible
                        ? `Pagado: ${formatCurrency(paidToOperator, currency || "ARS")} · Saldo: ${formatCurrency(outstandingToOperator, currency || "ARS")}`
                        : "El operador tiene un pago parcial — falta completar"
                }
            >
                <span className="h-2 w-2 rounded-full bg-amber-400 flex-shrink-0" aria-hidden="true" />
                Pago parcial al operador
                {/* Sin monto: misma regla firmada que "Operador pagado" e "impago". */}
            </span>
        );
    }

    if (status === "unpaid") {
        return (
            <span
                className="inline-flex items-center gap-1 text-[11px] font-semibold text-amber-700 dark:text-amber-400"
                data-testid="badge-operador-impago"
                title={
                    amountsVisible && netCost > 0
                        ? `Deuda con el operador: ${formatCurrency(netCost, currency || "ARS")}`
                        : "El operador todavía no tiene ningún pago registrado para este servicio"
                }
            >
                <span className="h-2 w-2 rounded-full bg-amber-400 flex-shrink-0" aria-hidden="true" />
                Operador impago
                {/* Sin monto (regla firmada Tanda 2, 2026-08-03): esta etiqueta puntual nunca
                    lleva plata al lado, aunque el usuario tenga permiso de costos — el número
                    sigue disponible en el title (arriba) y en la cuenta del proveedor. */}
            </span>
        );
    }

    // Status desconocido: no mostramos nada (degradación silenciosa)
    return null;
}
