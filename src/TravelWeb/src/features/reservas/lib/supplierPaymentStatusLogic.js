/**
 * Lógica pura del estado de pago al operador POR SERVICIO.
 *
 * Estas funciones NO dependen de React ni del DOM — se pueden importar
 * tanto en componentes como en tests de node:test sin configuración especial.
 *
 * Contexto (ADR-036 punto 4c):
 *   El backend expone GET /reservas/{id}/supplier-payment-status con el estado
 *   de pago al operador por cada servicio: "paid" / "partial" / "unpaid".
 *   El front tiene que cruzar esa lista con los servicios que ya renderiza,
 *   usando el par (recordKind, servicePublicId) como clave de unión.
 *
 * Regla de costos (guía UX 2026-06-21, P4=B):
 *   - El ESTADO (pagado/parcial/impago) lo ven TODOS.
 *   - Los MONTOS (netCost, paidToOperator, outstandingToOperator) solo se muestran
 *     si amountsVisible === true en la respuesta del backend (equivalente a tener
 *     cobranzas.see_cost; el backend ya aplica el enmascarado).
 */

/**
 * Busca el estado de pago al operador para UN servicio concreto dentro del DTO
 * que devuelve el endpoint supplier-payment-status.
 *
 * Clave de unión: (recordKind, servicePublicId).
 * Ambos campos llegan en minúscula desde el frontend; el backend también los
 * devuelve en minúscula (flight/hotel/transfer/package/assistance/generic).
 *
 * @param {string} recordKind - tipo del servicio (ej: "flight", "hotel")
 * @param {string|null} servicePublicId - publicId del servicio (uuid string)
 * @param {object|null} statusDto - DTO completo del endpoint (puede ser null si aún no cargó)
 * @returns {object|null} ServiceSupplierPaymentStatusDto del servicio, o null si no encontró
 */
export function buscarEstadoPagoServicio(recordKind, servicePublicId, statusDto) {
    if (!statusDto || !recordKind || !servicePublicId) return null;

    const servicios = statusDto.services || [];

    // El backend devuelve recordKind en minúscula (igual que el frontend).
    // Comparamos en minúscula para evitar discrepancias por capitalización.
    const kindNormalizado = String(recordKind).toLowerCase();
    const idNormalizado = String(servicePublicId).toLowerCase();

    const encontrado = servicios.find((s) => {
        const kindBackend = String(s.recordKind || "").toLowerCase();
        const idBackend = String(s.servicePublicId || "").toLowerCase();
        return kindBackend === kindNormalizado && idBackend === idNormalizado;
    });

    return encontrado || null;
}

/**
 * Convierte el status string del backend a una etiqueta visible en español.
 *
 * Valores posibles del backend: "paid" / "partial" / "unpaid".
 * Si el status es desconocido (o el servicio no tiene proveedor), devuelve null
 * para que el caller decida si mostrar algo o no.
 *
 * Guía UX 2026-06-21, P4=B:
 *   - "paid"    → "✔ Operador pagado"  (verde)
 *   - "partial" → "⚠ Parcialmente pagado" (ámbar)
 *   - "unpaid"  → "⚠ Operador impago" (ámbar)
 *
 * @param {string|null} status - valor del backend
 * @returns {{ texto: string, variante: 'pagado' | 'parcial' | 'impago' } | null}
 */
export function resolverEtiquetaEstadoPago(status) {
    if (status === "paid") {
        return { texto: "Operador pagado", variante: "pagado" };
    }
    if (status === "partial") {
        return { texto: "Pago parcial al operador", variante: "parcial" };
    }
    if (status === "unpaid") {
        return { texto: "Operador impago", variante: "impago" };
    }
    // status desconocido o null: no mostramos nada
    return null;
}

/**
 * Determina si los montos (costo, pagado, saldo) se pueden mostrar para un servicio.
 *
 * El backend ya aplica el enmascarado: si el usuario no tiene cobranzas.see_cost,
 * los montos llegan en 0 Y amountsVisible viene false.
 * Acá solo leemos amountsVisible del DTO para decidir si renderizar los montos.
 *
 * NO tomamos la decisión de permisos en el frontend — confiamos en lo que dice el backend.
 *
 * @param {object|null} statusDto - DTO raíz del endpoint (ReservaSupplierPaymentStatusDto)
 * @returns {boolean}
 */
export function puedenVerseMontos(statusDto) {
    if (!statusDto) return false;
    return Boolean(statusDto.amountsVisible);
}
