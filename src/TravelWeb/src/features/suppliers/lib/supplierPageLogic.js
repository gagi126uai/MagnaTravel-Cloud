/**
 * Lógica pura para la pantalla de cuenta corriente del proveedor.
 *
 * Estas funciones están separadas del componente para poder testearse
 * sin necesidad de montar React. Cada función tiene una responsabilidad clara.
 *
 * Funciones exportadas:
 *   - resolverMonedaPrincipalProveedor: qué moneda priorizar al pagar
 *   - calcularEquivalenteProveedor: conversión de moneda cruzada
 *   - construirPayloadPagoProveedor: armar el body del POST/PUT de pago
 */

/**
 * Determina la moneda principal para el pago al proveedor.
 *
 * Criterio: la primera moneda con balance positivo (hay deuda pendiente).
 * Si todo está saldado o a favor, devuelve la primera de la lista.
 * Fallback a "ARS" si no hay datos (no debería pasar en producción).
 *
 * @param {Array<{ currency: string, balance: number }>} balancesByCurrency
 * @returns {string} código de moneda, ej. "ARS" o "USD"
 */
export function resolverMonedaPrincipalProveedor(balancesByCurrency) {
    if (!Array.isArray(balancesByCurrency) || balancesByCurrency.length === 0) {
        return "ARS";
    }
    // Preferimos la moneda donde hay deuda activa: el cajero generalmente va a pagar eso primero
    const conDeuda = balancesByCurrency.find((b) => (b.balance ?? 0) > 0);
    return conDeuda ? conDeuda.currency : balancesByCurrency[0].currency;
}

/**
 * Calcula el monto equivalente en la moneda imputada para un pago cruzado.
 *
 * Pago cruzado = el cajero paga en una moneda (ej. ARS) pero baja deuda en otra (ej. USD).
 * El tipo de cambio convierte entre las dos.
 *
 * Fórmulas:
 *   - ARS → USD: equivalente = monto / TC  (1 USD cuesta TC pesos)
 *   - USD → ARS: equivalente = monto × TC
 *
 * @param {string|number} monto — el monto que el cajero ingresó
 * @param {string|number} tipoCambio — TC ingresado por el usuario
 * @param {string} monedaCobro — moneda en la que se paga al proveedor
 * @param {string} saldoImputado — moneda del saldo que se reduce
 * @returns {number|null} monto equivalente, o null si no aplica o faltan datos
 */
export function calcularEquivalenteProveedor(monto, tipoCambio, monedaCobro, saldoImputado) {
    // Sin cruce de moneda: no hay equivalente que calcular
    if (!monedaCobro || !saldoImputado || monedaCobro === saldoImputado) return null;

    const tc = parseFloat(tipoCambio);
    const m = parseFloat(monto);
    if (isNaN(tc) || tc <= 0 || isNaN(m) || m <= 0) return null;

    if (monedaCobro === "ARS" && saldoImputado === "USD") return m / tc;
    if (monedaCobro === "USD" && saldoImputado === "ARS") return m * tc;

    // Combinación de monedas no soportada (ej. EUR/USD): no calculamos
    return null;
}

/**
 * Construye el payload para POST /suppliers/{id}/payments (nuevo pago)
 * o PUT /suppliers/{id}/payments/{paymentId} (editar pago existente).
 *
 * IMPORTANTE: los campos de tipo de cambio (imputedCurrency, exchangeRate,
 * exchangeRateSource, exchangeRateAt, imputedAmount) SOLO se incluyen cuando
 * esCruzado=true. Si se enviaran siempre, el backend los rechaza cuando
 * la moneda del pago y del saldo son iguales.
 *
 * exchangeRateSource es un INT (enum del backend), no string.
 * El <select> devuelve string → convertir con Number() al armar el payload.
 *
 * @param {object} params
 * @param {string|number} params.monto           — monto que ingresó el usuario
 * @param {string}        params.monedaPago       — "ARS" o "USD"
 * @param {string}        params.metodo           — "Transfer"/"Cash"/"Check"/"Card"
 * @param {string}        params.fecha            — fecha en formato "YYYY-MM-DD"
 * @param {string}        params.referencia       — número de comprobante (puede ser vacío)
 * @param {string}        params.notas            — notas internas (puede ser vacío)
 * @param {string|null}   params.reservaId        — publicId de la reserva imputada (opcional)
 * @param {string|null}   params.serviceRecordKind — tipo del servicio imputado (opcional)
 * @param {string|null}   params.servicePublicId   — publicId del servicio imputado (opcional)
 * @param {boolean}       params.esCruzado         — true si monedaPago ≠ saldoImputado
 * @param {string}        params.saldoImputado     — moneda del saldo que se reduce (cruzado)
 * @param {string|number} params.tipoCambio        — tipo de cambio (cruzado)
 * @param {number|string} params.fuenteTC          — fuente TC como int (viene de <select> como string)
 * @param {string}        params.fechaTC           — fecha del TC en "YYYY-MM-DD" (cruzado)
 * @param {number|null}   params.montoEquivalente  — monto ya convertido a saldoImputado (cruzado)
 * @returns {object} payload listo para enviar como JSON al backend
 */
export function construirPayloadPagoProveedor({
    monto,
    monedaPago,
    metodo,
    fecha,
    referencia,
    notas,
    reservaId,
    serviceRecordKind,
    servicePublicId,
    esCruzado,
    saldoImputado,
    tipoCambio,
    fuenteTC,
    fechaTC,
    montoEquivalente,
}) {
    // Payload base: siempre va, sea pago simple o cruzado
    const base = {
        amount: parseFloat(monto),
        currency: monedaPago,
        method: metodo,
        paidAt: new Date(fecha).toISOString(),
        reference: (referencia || "").trim() || null,
        notes: (notas || "").trim() || null,
        reservaId: reservaId || null,
        serviceRecordKind: serviceRecordKind || null,
        servicePublicId: servicePublicId || null,
    };

    if (!esCruzado) {
        return base;
    }

    // Campos adicionales para pago cruzado (moneda de pago ≠ moneda del saldo)
    return {
        ...base,
        imputedCurrency: saldoImputado,
        exchangeRate: parseFloat(tipoCambio),
        exchangeRateSource: Number(fuenteTC), // el backend espera int, el <select> devuelve string
        exchangeRateAt: new Date(fechaTC).toISOString(),
        imputedAmount: montoEquivalente,
    };
}
