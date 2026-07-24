import { formatCurrency } from "../../../lib/utils.js";

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
 *   - ordenarBloquesPesosPrimero: orden de los recuadros del encabezado (Fase D)
 *   - debeMostrarseEnGrisNeutro: cuándo un recuadro del encabezado va en gris (Fase D)
 *   - aplanarReembolsosPendientesPorMoneda: filas seleccionables de "Registrar reembolso recibido" (§4)
 *   - validarFormularioReembolsoRecibido: validación local antes de llamar al backend (§4)
 *   - construirTextoCuentaReembolso: desglose "Pagaste − Multa [− Ya devuelto] = te devuelven" o el
 *     motivo en criollo cuando el estimado da $0 (decisiones 1 y 4, spec 2026-07-03)
 *   - filtrarServiciosPorMonedaDePago: pre-chequeo (a) de la Tanda 1 (contrato pantalla-motor,
 *     2026-07-18) — el selector de servicio solo lista los que están en la moneda del pago
 *   - hayServiciosDelProveedorEnReserva: pre-chequeo (b) de la misma tanda — si la reserva
 *     elegida no tiene NINGÚN servicio de este proveedor, hay que avisar antes de confirmar
 *   - resolverReservaImputadaEnEdicion / incluirReservaImputadaEnLista /
 *     resolverServicioImputadoEnEdicion / resolverServicioSinteticoEnEdicion: Bug #15
 *     (Tanda 4, 2026-07-24) — al editar un pago, precargan la imputación real que ya trae
 *     el DTO del pago (antes se perdía de vista); la versión "sintética" cubre la carrera
 *     de red mientras la lista de servicios todavía no cargó (fix N1, review Tanda 4)
 *   - resolverDeudaDeReferenciaParaSobrepago: Bug #3 (Tanda 4) — deuda contra la que se
 *     compara un pago nuevo o editado para el aviso de sobrepago
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
 * @param {string|null}   params.settlesOperatorChargePublicId — cargo facturado aparte que queda liquidado
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
    settlesOperatorChargePublicId,
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

    if (settlesOperatorChargePublicId) {
        base.settlesOperatorChargePublicId = settlesOperatorChargePublicId;
    }

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

// ─── Tanda 1 (contrato pantalla-motor, 2026-07-18): pre-chequeos del pago al proveedor ──
// Spec: docs/ux/2026-07-18-t1-t2-contrato-pantalla-motor.md, sección TANDA 1.
// Objetivo: atajar ANTES del clic los dos casos de error que el back ya rechazaba
// (moneda del pago ≠ moneda del servicio / reserva sin servicios de este proveedor),
// para que el vendedor ni siquiera pueda armar el pago mal.

/**
 * Filtra los servicios de una reserva para quedarse solo con los que están en la
 * MISMA moneda que el pago (pre-chequeo (a)). Así el selector nunca deja elegir un
 * servicio cuya moneda no coincide con la que el cajero eligió para pagar.
 *
 * Igual que en el backend (ADR-021 §15.4): `currency` nulo/vacío en un servicio
 * legacy significa ARS (se normaliza acá con el mismo criterio que el resto del front).
 *
 * @param {Array<{ currency?: string|null }>} servicios
 * @param {string} monedaPago — "ARS" | "USD"
 * @returns {Array} subconjunto de servicios en esa moneda
 */
export function filtrarServiciosPorMonedaDePago(servicios, monedaPago) {
    const lista = Array.isArray(servicios) ? servicios : [];
    return lista.filter((s) => (s.currency || "ARS") === monedaPago);
}

/**
 * Pre-chequeo (b): ¿esta reserva tiene AL MENOS UN servicio de este proveedor
 * (en cualquier moneda)? Si no, hay que avisar ANTES de habilitar "Confirmar" en
 * vez de dejar que el vendedor se entere recién con el 409 del backend.
 *
 * OJO: recibe la lista SIN filtrar por moneda (todas las monedas), porque la
 * pregunta es "¿existe algún servicio de este proveedor acá?", no "¿hay alguno
 * en la moneda que elegí ahora?" — ese segundo caso lo cubre el filtro (a) y no
 * bloquea nada (el vendedor puede simplemente no imputar a un servicio puntual).
 *
 * @param {Array} serviciosDeLaReserva — servicios de este proveedor en la reserva elegida
 * @returns {boolean} true si hay al menos uno
 */
export function hayServiciosDelProveedorEnReserva(serviciosDeLaReserva) {
    return Array.isArray(serviciosDeLaReserva) && serviciosDeLaReserva.length > 0;
}

/**
 * Ordena los bloques de moneda del encabezado ("Le debo" / "Me tiene que devolver" /
 * "Saldo a favor") para que pesos aparezca siempre primero y dólares después, como pide
 * la spec de la Fase D (2026-07-01). Cualquier otra moneda futura queda al final.
 *
 * No muta el array de entrada (devuelve uno nuevo).
 *
 * @param {Array<{ currency: string }>} currencies — bloques de SupplierAccountStatementDto.currencies
 * @returns {Array<{ currency: string }>} copia ordenada
 */
export function ordenarBloquesPesosPrimero(currencies) {
    const bloques = Array.isArray(currencies) ? currencies : [];
    return [...bloques].sort((a, b) => {
        if (a.currency === "USD" && b.currency !== "USD") return 1;
        if (b.currency === "USD" && a.currency !== "USD") return -1;
        return 0;
    });
}

/**
 * Decide si un recuadro del encabezado ("Le debo" / "Me tiene que devolver" / "Saldo a favor")
 * debe pintarse en gris neutro en vez de con su color propio (rojo/naranja/verde).
 *
 * Dos motivos posibles, ambos independientes de qué recuadro sea:
 *   1. El usuario no tiene permiso de ver costos (cobranzas.see_cost) → SIEMPRE gris,
 *      nunca revelamos si hay deuda/reembolso/saldo a quien no puede verlo.
 *   2. El monto es $0 (con tolerancia de redondeo de medio centavo) → gris neutro,
 *      porque no hay nada que remarcar (regla de la spec: "0 = gris neutro").
 *
 * @param {number|null|undefined} monto
 * @param {boolean} puedeVerMontos
 * @returns {boolean} true si el recuadro debe ir en gris neutro
 */
export function debeMostrarseEnGrisNeutro(monto, puedeVerMontos) {
    if (!puedeVerMontos) return true;
    const numero = Number(monto ?? 0);
    return Math.abs(numero) < 0.005;
}

/**
 * Aplana la lista de "reembolsos pendientes" (OperatorRefundPendingItemDto[]) a filas
 * seleccionables para el selector obligatorio de la ficha "Registrar reembolso recibido" (§4).
 *
 * Por qué aplanar: cada item del backend es UNA cancelación, pero puede tener reembolsos
 * estimados en VARIAS monedas a la vez (`estimatedRefundsByCurrency[]`). La spec pide que el
 * usuario elija "una fila = una anulación + una moneda" (no un monto suelto sin destino), así
 * que una cancelación con estimado en ARS y en USD se convierte en DOS filas seleccionables,
 * cada una con su propia moneda fija.
 *
 * Cuenta del operador (2026-07-03): además de lo estimado, cada fila ahora también trae los
 * campos de la "cuenta completa" (decisiones 1 y 4) y de RESTOS (conciliación) que vienen del
 * ITEM del backend — paidToOperator/penaltyRetained/amountReceived/zeroRefundReason están POR
 * MONEDA (vienen de la línea); penaltyPendingConfirmation/rowStatus/canRegisterRefund/
 * reservaPublicId son del ITEM completo, se copian igual a cada fila de ese item.
 *
 * @param {Array<object>} items — OperatorRefundPendingItemDto[] del backend
 * @returns {Array<{
 *   key: string,
 *   bookingCancellationPublicId: string,
 *   reservaPublicId: string,
 *   numeroReserva: string,
 *   clienteNombre: string,
 *   currency: string,
 *   estimatedAmount: number,
 *   amountsMasked: boolean,
 *   paidToOperator: number,
 *   penaltyRetained: number,
 *   amountReceived: number,
 *   zeroRefundReason: string|null,
 *   penaltyPendingConfirmation: boolean,
 *   rowStatus: number,
 *   canRegisterRefund: boolean,
 * }>}
 */
export function aplanarReembolsosPendientesPorMoneda(items) {
    const filas = [];
    for (const item of Array.isArray(items) ? items : []) {
        const montosPorMoneda = Array.isArray(item?.estimatedRefundsByCurrency)
            ? item.estimatedRefundsByCurrency
            : [];
        for (const linea of montosPorMoneda) {
            filas.push({
                key: `${item.bookingCancellationPublicId}-${linea.currency}`,
                bookingCancellationPublicId: item.bookingCancellationPublicId,
                reservaPublicId: item.reservaPublicId ?? "",
                numeroReserva: item.numeroReserva ?? "",
                clienteNombre: item.clienteNombre ?? "",
                currency: linea.currency,
                estimatedAmount: linea.estimatedAmount ?? 0,
                amountsMasked: Boolean(item.amountsMasked),
                paidToOperator: linea.paidToOperator ?? 0,
                penaltyRetained: linea.penaltyRetained ?? 0,
                amountReceived: linea.amountReceived ?? 0,
                zeroRefundReason: linea.zeroRefundReason ?? null,
                penaltyPendingConfirmation: Boolean(item.penaltyPendingConfirmation),
                rowStatus: item.rowStatus ?? 0,
                canRegisterRefund: Boolean(item.canRegisterRefund),
            });
        }
    }
    return filas;
}

// ─── Decisiones 1 y 4 (spec 2026-07-03): la "cuenta completa" de un reembolso pendiente ──

/**
 * Motivos en criollo cuando el estimado da $0 (decisión 4 / P4=A). El backend ya calculó
 * CUÁL de los tres motivos aplica (ZeroRefundReason) — el front NUNCA resta montos para
 * adivinarlo, solo traduce el código a texto.
 */
const ZERO_REFUND_REASON_LABELS = {
    NothingPaidToOperator: "Todavía no le pagaste nada al operador por este viaje.",
    PenaltyCoversAll: "No hay nada para devolver: la multa del operador se quedó con todo lo que le pagaste.",
    FullyRefunded: "Ya te devolvió todo por este viaje.",
};

/**
 * Arma el texto de la "cuenta completa" de una fila de reembolso pendiente (decisión 1 / P3=A):
 * "Pagaste US$ 500 − Multa del operador US$ 100 = te devuelven US$ 400 (estimado)."
 *
 * Con RESTOS (AmountReceived > 0), agrega el término que ya se cobró para que la cuenta
 * cierre: "Pagaste US$ 500 − Multa del operador US$ 100 − Ya devuelto US$ 50 = te devuelven
 * US$ 350 (estimado)." — el invariante del backend es Estimado = Pagado − Multa − Recibido.
 *
 * Cuando el estimado da $0, en vez de la cuenta se explica el motivo (decisión 4 / P4=A).
 * Cuando los montos están enmascarados (sin cobranzas.see_cost), se muestra "—" — el motivo
 * de $0 NO se enmascara (no es un monto, lo expone siempre el backend).
 *
 * @param {{ estimatedAmount:number, paidToOperator:number, penaltyRetained:number, amountReceived:number, zeroRefundReason:string|null, currency:string, amountsMasked:boolean }} fila
 * @returns {string}
 */
export function construirTextoCuentaReembolso(fila) {
    if (!fila) return "";

    if (fila.amountsMasked) {
        return "—";
    }

    if (fila.estimatedAmount === 0) {
        return ZERO_REFUND_REASON_LABELS[fila.zeroRefundReason] ?? "No hay reembolso estimado por este viaje.";
    }

    let texto = `Pagaste ${formatCurrency(fila.paidToOperator, fila.currency)}`
        + ` − Multa del operador ${formatCurrency(fila.penaltyRetained, fila.currency)}`;

    if (fila.amountReceived > 0) {
        texto += ` − Ya devuelto ${formatCurrency(fila.amountReceived, fila.currency)}`;
    }

    texto += ` = te devuelven ${formatCurrency(fila.estimatedAmount, fila.currency)} (estimado).`;
    return texto;
}

/**
 * Valida el formulario de "Registrar reembolso recibido" ANTES de llamar al backend.
 *
 * Esta validación es solo para UX (mensajes claros e inmediatos); el backend
 * (RecordAndAllocateRefundRequest) es quien tiene la última palabra sobre lo que
 * es correcto — nunca hay que confiar solo en esto para la integridad de la plata.
 *
 * Reglas (spec §4):
 *   1. Hay que elegir un reembolso pendiente (no se permite un monto suelto sin destino).
 *   2. El monto tiene que ser mayor a 0.
 *   3. Si el estimado del pendiente elegido es conocido (no enmascarado), el monto no
 *      puede superarlo — es una alerta temprana, no un tope duro (el operador puede
 *      haber devuelto un poco más por redondeo; el backend decide si lo acepta).
 *   4. La fecha es obligatoria.
 *   5. (2026-07-03, RESTOS) La fila elegida tiene que admitir el registro (canRegisterRefund).
 *      El selector ya deshabilita las filas no registrables, esto es defensa en profundidad
 *      por si algo cambia de estado mientras la ficha estaba abierta.
 *
 * @param {{ filaSeleccionada: object|null, monto: string|number, fecha: string }} datos
 * @returns {string|null} mensaje de error en criollo, o null si el formulario es válido
 */
export function validarFormularioReembolsoRecibido({ filaSeleccionada, monto, fecha }) {
    if (!filaSeleccionada) {
        return "Elegí a qué reembolso pendiente corresponde antes de confirmar.";
    }

    if (filaSeleccionada.canRegisterRefund === false) {
        return "Este reembolso todavía no se puede registrar. Revisá el estado de la anulación.";
    }

    const montoNumero = parseFloat(monto);
    if (!monto || isNaN(montoNumero) || montoNumero <= 0) {
        return "El monto tiene que ser mayor a 0.";
    }

    // Solo comparamos contra el estimado si lo conocemos (sin permiso de ver costos,
    // el estimado llega en 0 y compararlo generaría un error falso).
    if (!filaSeleccionada.amountsMasked && montoNumero > filaSeleccionada.estimatedAmount) {
        return `El monto no puede superar el estimado de este reembolso (${formatCurrency(filaSeleccionada.estimatedAmount, filaSeleccionada.currency)}).`;
    }

    if (!fecha) {
        return "La fecha es obligatoria.";
    }

    return null;
}

// ─── Rediseño "Registrar pago" (2026-07-20): flujo de 2 pasos aprobado por el dueño ──────
// Spec: docs/architecture/2026-07-20-analisis-cuenta-proveedor-vs-erps.md, sección 5.
// Paso 1 = elegir A QUÉ reserva/servicio se paga (grilla, no un desplegable escondido al
// final del formulario). Paso 2 = confirmar monto/método con el destino ya fijado. Estas
// funciones son las que arman los datos de esos dos pasos:
//   - armarFilasDeudaPorReserva / agruparServiciosPorReserva / construirDetalleFilaDeuda:
//     arman la grilla del Paso 1.
//   - construirMensajeExitoPago: arma el cartel de éxito de después de guardar, usando
//     SIEMPRE el `impact` que devuelve el backend (nunca un cálculo hecho a mano acá).

/**
 * Convierte la respuesta de GET /suppliers/{id}/account/debt-by-reserva (agrupada por
 * reserva, con una lista de monedas adentro de cada una) en FILAS PLANAS para la grilla
 * del Paso 1: una fila por cada combinación reserva+moneda que tenga deuda real (> 0).
 *
 * Por qué filtramos por saldo > 0: `debt-by-reserva` también puede traer líneas en 0 o
 * negativas (sobrepago/saldo a favor de esa reserva puntual) — el Paso 1 es "¿qué le debés
 * a este proveedor?", no un listado general de todos los movimientos.
 *
 * FIX review 2026-07-21 (bloqueante frontend-reviewer #2): ese filtro por saldo asumía que
 * el número que llega SIEMPRE representa la deuda real. Pero sin el permiso
 * `cobranzas.see_cost`, el backend enmascara `Balance` a 0 en TODAS las líneas por igual
 * (no solo en las que de verdad están saldadas) — filtrar por `balance > 0` dejaba la
 * grilla siempre vacía para ese perfil, aunque SÍ hubiera deuda real, y el cajero quedaba
 * sin poder elegir ninguna reserva (rompía la imputación, no solo la vista de montos).
 * Con `puedeVerMontos=false` mostramos TODAS las filas igual (la columna "Debe" se pinta
 * como "—" en el componente) — el backend vuelve a validar la imputación real al confirmar
 * el pago, así que no hay riesgo de negocio en mostrar de más acá.
 *
 * @param {Array<{reservaPublicId:string, numeroReserva?:string, fileName?:string, currencies?:Array<{currency:string, balance:number}>}>} reservasConDeuda
 * @param {{puedeVerMontos?: boolean}} [opciones] — puedeVerMontos=false desactiva el filtro por saldo (ver arriba)
 * @returns {Array<{reservaPublicId:string, numeroReserva:string, fileName:string|null, currency:string, balance:number}>}
 */
export function armarFilasDeudaPorReserva(reservasConDeuda, { puedeVerMontos = true } = {}) {
    const filas = [];
    for (const reserva of Array.isArray(reservasConDeuda) ? reservasConDeuda : []) {
        const monedas = Array.isArray(reserva?.currencies) ? reserva.currencies : [];
        for (const linea of monedas) {
            const balance = Number(linea?.balance ?? 0);
            // Umbral de medio centavo: mismo criterio que el resto de la pantalla
            // (ver debeMostrarseEnGrisNeutro) para no mostrar "deuda" por un resto de redondeo.
            // Sin permiso de ver costos, el número está enmascarado (siempre 0) y no sirve
            // como filtro — se incluyen todas las combinaciones reserva+moneda que trajo el
            // backend, sin importar el balance.
            if (puedeVerMontos && balance <= 0.005) continue;
            filas.push({
                reservaPublicId: reserva.reservaPublicId,
                numeroReserva: reserva.numeroReserva || "Reserva",
                fileName: reserva.fileName || null,
                currency: linea.currency,
                balance,
            });
        }
    }
    return filas;
}

/**
 * Agrupa la lista PLANA de servicios del proveedor (GET /suppliers/{id}/account/services)
 * por reserva, para poder armar el texto de "Detalle" de cada fila de la grilla del Paso 1
 * sin pedirle un endpoint nuevo al backend — `debt-by-reserva` solo trae el TOTAL por
 * moneda, no el desglose por servicio.
 *
 * @param {Array<{reservaPublicId?:string, type?:string, description?:string, currency?:string}>} servicios
 * @returns {Record<string, Array<{type:string, description:string|null, currency:string}>>} mapa reservaPublicId → servicios de esa reserva
 */
export function agruparServiciosPorReserva(servicios) {
    const mapa = {};
    for (const servicio of Array.isArray(servicios) ? servicios : []) {
        const clave = servicio?.reservaPublicId ? String(servicio.reservaPublicId) : null;
        if (!clave) continue; // sin reserva asociada, no aporta al detalle de ninguna fila
        if (!mapa[clave]) mapa[clave] = [];
        mapa[clave].push({
            type: servicio.type || "Servicio",
            description: servicio.description || null,
            currency: servicio.currency || "ARS",
        });
    }
    return mapa;
}

/**
 * Arma el texto de la columna "Detalle" de una fila de la grilla del Paso 1
 * (ej. "Hotel — Bariloche"), mirando los servicios de ESA reserva en la MISMA moneda de
 * la fila. Si hay más de un servicio, se nombra el primero y se suman los demás
 * ("y 2 más") para no romper el ancho de la grilla. Si no encontramos ningún servicio
 * (por ejemplo, la deuda viene de un cargo del operador facturado aparte sin servicio
 * puntual asociado), usamos el nombre de la reserva como respaldo.
 *
 * @param {{reservaPublicId:string, currency:string, fileName?:string|null}} fila
 * @param {Record<string, Array<{type:string, description:string|null, currency:string}>>} serviciosPorReserva
 * @returns {string}
 */
export function construirDetalleFilaDeuda(fila, serviciosPorReserva) {
    const servicios = (serviciosPorReserva || {})[String(fila?.reservaPublicId)] || [];
    const enEstaMoneda = servicios.filter((s) => (s.currency || "ARS") === fila?.currency);

    if (enEstaMoneda.length === 0) {
        return fila?.fileName || "—";
    }

    const etiquetas = enEstaMoneda.map((s) => (s.description ? `${s.type} — ${s.description}` : s.type));
    if (etiquetas.length === 1) return etiquetas[0];
    return `${etiquetas[0]} y ${etiquetas.length - 1} más`;
}

/**
 * Fix #46 (Tanda 3, 2026-07-24): decide qué pasa con el campo "Monto" cuando el cajero
 * cambia el servicio elegido en el selector de imputación del Paso 2.
 *
 * Antes de este fix, elegir un servicio puntual NO tocaba el monto: quedaba con el saldo
 * de la FILA completa que se precargó en el Paso 1 (`fila.balance`, que puede sumar VARIOS
 * servicios de esa reserva+moneda). El cajero terminaba pagando de más o de menos sin
 * darse cuenta al refinar hacia un servicio concreto.
 *
 * `outstandingToOperator` (el saldo REAL pendiente de ESE servicio, descontando pagos
 * parciales previos) vive en GET /reservas/{id}/supplier-payment-status — esta ficha no lo
 * carga hoy (evita un pedido más por cada apertura, y acá se puede estar pagando deuda de
 * varias reservas distintas, no de una sola). Mientras tanto se usa `netCost` del servicio
 * como aproximación: es exacto si el servicio no tenía pagos parciales previos, pero puede
 * sobreestimar el saldo si ya se le pagó algo antes.
 *
 * @param {{ servicioElegido: {netCost?: number}|null, filaBalance: number|null|undefined, puedeVerMontos: boolean }} params
 * @returns {{ debeActualizarMonto: boolean, nuevoMonto: string|null }}
 */
export function resolverMontoAlElegirServicio({ servicioElegido, filaBalance, puedeVerMontos }) {
    // Sin permiso de ver costos, el backend enmascara todo a 0: no autocompletamos nada,
    // el cajero tipea el monto a mano (mismo criterio que el resto de la ficha).
    if (!puedeVerMontos) {
        return { debeActualizarMonto: false, nuevoMonto: null };
    }

    if (!servicioElegido) {
        // El cajero volvió a "Sin imputar a un servicio específico": restauramos el saldo
        // de la fila completa que eligió en el Paso 1. Si no hay una fila de origen (por
        // ejemplo, en modo edición no existe ese concepto), no tocamos el monto — ya viene
        // precargado con el importe del pago que se está editando.
        if (filaBalance == null) return { debeActualizarMonto: false, nuevoMonto: null };
        return { debeActualizarMonto: true, nuevoMonto: String(filaBalance) };
    }

    return { debeActualizarMonto: true, nuevoMonto: String(servicioElegido.netCost ?? 0) };
}

// ─── Bug #15 (Tanda 4, 2026-07-24): precargar la imputación real al editar un pago ───────
// Antes, abrir "Editar" sobre un pago a proveedor SIEMPRE arrancaba en "Sin imputar",
// aunque el DTO del pago (SupplierPaymentDto) trae `reservaPublicId`/`servicePublicId` con
// la imputación real. El vendedor perdía de vista adónde había ido la plata de ese pago.

/**
 * Busca la reserva imputada de un pago en edición dentro de `reservasConDeuda` (lo normal:
 * esa reserva sigue debiendo). Si YA NO aparece ahí — porque justamente ESTE pago la saldó
 * por completo — arma una fila mínima con los datos que sí trae el DTO del pago, para que
 * el desplegable de imputación la siga mostrando en vez de quedar vacío.
 *
 * @param {{reservaPublicId?:string|null, numeroReserva?:string|null, fileName?:string|null}|null} paymentToEdit
 * @param {Array<{reservaPublicId:string}>} reservasConDeuda
 * @returns {{reservaPublicId:string, numeroReserva:string, fileName:string|null}|null} null si el pago era "a cuenta" (sin reserva)
 */
export function resolverReservaImputadaEnEdicion(paymentToEdit, reservasConDeuda) {
    const reservaId = paymentToEdit?.reservaPublicId;
    if (!reservaId) return null; // pago "a cuenta": no hay reserva que precargar

    const lista = Array.isArray(reservasConDeuda) ? reservasConDeuda : [];
    const enLaLista = lista.find((r) => String(r.reservaPublicId) === String(reservaId));
    if (enLaLista) return enLaLista;

    // Ya no debe (este mismo pago la saldó): la mostramos igual con lo que trae el DTO.
    return {
        reservaPublicId: reservaId,
        numeroReserva: paymentToEdit.numeroReserva || "Reserva",
        fileName: paymentToEdit.fileName || null,
    };
}

/**
 * Agrega `reservaImputada` a la lista de reservas con deuda SI todavía no está ahí (caso
 * de la reserva ya saldada por este mismo pago, ver `resolverReservaImputadaEnEdicion`).
 * Sin esto, el <select> de imputación en edición mostraría un value que no matchea
 * ninguna <option> y el desplegable se vería vacío pese a haber una imputación real.
 *
 * @param {Array<{reservaPublicId:string}>} reservasConDeuda
 * @param {{reservaPublicId:string}|null} reservaImputada
 * @returns {Array} lista lista para renderizar en el <select>, sin duplicados
 */
export function incluirReservaImputadaEnLista(reservasConDeuda, reservaImputada) {
    const lista = Array.isArray(reservasConDeuda) ? reservasConDeuda : [];
    if (!reservaImputada) return lista;
    const yaEsta = lista.some((r) => String(r.reservaPublicId) === String(reservaImputada.reservaPublicId));
    return yaEsta ? lista : [...lista, reservaImputada];
}

/**
 * Igual que `resolverReservaImputadaEnEdicion` pero para el SERVICIO puntual imputado
 * (cuando el pago se aplicó a un servicio concreto de la reserva, no a toda ella). Busca
 * en la lista de servicios YA cargados de esa reserva; si el pago no tenía servicio
 * imputado, o el servicio ya no está en la lista, no fuerza ninguna selección.
 *
 * @param {{servicePublicId?:string|null, serviceRecordKind?:string|null}|null} paymentToEdit
 * @param {Array<{publicId:string|number, type?:string, description?:string, netCost?:number, currency?:string}>} serviciosDeLaReserva
 * @returns {{servicePublicId:string, serviceRecordKind:string, descripcion:string, netCost?:number, currency?:string}|null}
 */
export function resolverServicioImputadoEnEdicion(paymentToEdit, serviciosDeLaReserva) {
    const servicioId = paymentToEdit?.servicePublicId;
    if (!servicioId) return null;

    const lista = Array.isArray(serviciosDeLaReserva) ? serviciosDeLaReserva : [];
    const encontrado = lista.find((s) => String(s.publicId) === String(servicioId));
    if (!encontrado) return null; // ya no está en la lista cargada: no forzamos una selección rara

    return {
        servicePublicId: String(encontrado.publicId),
        serviceRecordKind: paymentToEdit.serviceRecordKind || "generic",
        descripcion: encontrado.description || encontrado.type,
        netCost: encontrado.netCost,
        currency: encontrado.currency,
    };
}

/**
 * Fix N1 (review de seguridad, Tanda 4, 2026-07-24): siembra el servicio imputado ANTES
 * de que `serviciosReserva` termine de cargar (carrera de red). Sin esto, si el cajero
 * guarda el pago editado justo en ese hueco, el PUT salía con `servicePublicId=null` y la
 * imputación fina se degradaba a nivel-reserva EN SILENCIO — el pago quedaba imputado a
 * TODA la reserva en vez de al servicio puntual original, sin ningún aviso.
 *
 * Esta fila sintética SOLO trae el id y el tipo (lo mínimo para que el payload nunca
 * pierda el dato); `resolverServicioImputadoEnEdicion` la reemplaza por el objeto
 * completo (con descripción/costo/moneda reales) apenas `serviciosReserva` carga y
 * encuentra el servicio. Si nunca lo encuentra (caso raro), la fila sintética queda —
 * el id sigue siendo correcto igual, aunque el desplegable no muestre su descripción.
 *
 * @param {{servicePublicId?:string|null, serviceRecordKind?:string|null}|null} paymentToEdit
 * @returns {{servicePublicId:string, serviceRecordKind:string, descripcion:string}|null}
 */
export function resolverServicioSinteticoEnEdicion(paymentToEdit) {
    const servicioId = paymentToEdit?.servicePublicId;
    if (!servicioId) return null; // el pago no tenía servicio imputado: nada que sembrar

    return {
        servicePublicId: String(servicioId),
        serviceRecordKind: paymentToEdit.serviceRecordKind || "generic",
        // Placeholder hasta que resolverServicioImputadoEnEdicion lo reemplace con el
        // objeto real — nunca se manda al backend, solo se ve un instante en el <select>.
        descripcion: "Servicio imputado",
    };
}

// ─── Bug #3 (Tanda 4, 2026-07-24): aviso de sobrepago al proveedor ────────────────────────
// Decisión FIRMADA del dueño (P-14): pagar de más al proveedor NUNCA se bloquea, pero
// SIEMPRE se avisa el excedente antes de guardar (queda como saldo a favor nuestro con
// el operador). calcularExcedente/construirConfirmacionSobrepagoProveedor viven en
// lib/overpaymentConfirmLogic.js (se comparten con RegistrarCobroInline y
// CustomerPaymentModal); acá solo resolvemos la DEUDA de referencia, que es específica de
// esta pantalla (reserva elegida en el Paso 1, o el saldo global del proveedor).

/**
 * Calcula la deuda de referencia contra la que comparar un pago (nuevo o editado) para el
 * aviso de sobrepago. Devuelve null cuando no hay un dato confiable para comparar — en ese
 * caso NO se muestra ningún aviso (nunca se inventa un número).
 *
 * @param {object} params
 * @param {{reservaPublicId?:string, currency?:string, debe?:number, currencies?:Array<{currency:string,balance:number}>}|null} params.reservaSeleccionada
 * @param {string} params.saldoImputado — moneda a la que se está imputando el pago
 * @param {boolean} params.esCruzado — true si la moneda del EFECTIVO (monedaPago) difiere de saldoImputado
 * @param {Array<{currency:string, balance:number}>} params.balancesByCurrency — saldo global del proveedor (fallback)
 * @param {{amount?:number, currency?:string}|null} [params.paymentToEdit] — solo en edición: para "devolver" el propio monto ya restado
 * @returns {number|null}
 */
export function resolverDeudaDeReferenciaParaSobrepago({
    reservaSeleccionada,
    saldoImputado,
    esCruzado,
    balancesByCurrency,
    paymentToEdit,
}) {
    if (!reservaSeleccionada) return null; // "pago a cuenta" o cargo facturado aparte: no hay deuda puntual que comparar

    let deuda = null;

    // Forma "fila elegida en el Paso 1" (pago nuevo): currency/debe singulares. Ojo: NO
    // filtramos por `esCruzado` acá — esa bandera describe si el EFECTIVO (monedaPago)
    // difiere de saldoImputado, no si reservaSeleccionada.currency sirve como referencia.
    // El único chequeo que importa es que la moneda de `debe` coincida con saldoImputado
    // (si el cajero cambió "Imputar a" a otra moneda, esta comparación ya no matchea sola).
    if (typeof reservaSeleccionada.debe === "number" && reservaSeleccionada.currency === saldoImputado) {
        deuda = reservaSeleccionada.debe;
    } else if (Array.isArray(reservaSeleccionada.currencies)) {
        // Forma "cruda" de reservasConDeuda (edición, Bug #15): currencies[] con balance por moneda.
        const linea = reservaSeleccionada.currencies.find((c) => c.currency === saldoImputado);
        if (linea) deuda = Number(linea.balance);
    }

    // Sin dato de ESA reserva puntual: usamos el saldo global del proveedor como aproximación.
    if (deuda == null) {
        deuda = balancesByCurrency?.find((b) => b.currency === saldoImputado)?.balance ?? null;
    }

    if (deuda == null) return null;

    // Al editar un pago simple que YA restó su propio monto del saldo, hay que devolvérselo
    // para comparar contra lo que se debía ANTES de este pago.
    if (paymentToEdit && !esCruzado && paymentToEdit.currency === saldoImputado) {
        deuda += Number(paymentToEdit.amount || 0);
    }

    return deuda;
}

/**
 * Arma el mensaje del cartel de éxito que se muestra después de registrar un pago NUEVO
 * (reemplaza el cierre silencioso que tenía la ficha antes del rediseño). Usa el `impact`
 * que devuelve el backend en la respuesta del POST — NUNCA recalculamos el saldo restante
 * acá: si lo hiciéramos a mano, un pago cruzado de moneda o un cargo liquidado aparte
 * podrían mostrar un número distinto al que el resto de la pantalla ve un segundo después
 * (el backend recalcula con el mismo motor que "Deuda por reserva").
 *
 * Cinco casos:
 *   1. Sin `impact` (`null`/`undefined` — no debería pasar en producción, pero el POST YA
 *      se guardó del lado del servidor cuando esta función se llama): mensaje genérico
 *      "Pago registrado.". FIX bloqueante (review 2026-07-21, frontend-reviewer +
 *      security-data-risk-reviewer, convergente): antes esta función devolvía `null` en
 *      este caso, y el componente interpretaba "sin cartel" como "el pago no se guardó" —
 *      volvía a mostrar el formulario con el botón "Confirmar pago" habilitado, y como el
 *      pago "a cuenta" no tiene tope ni idempotencia, un segundo clic generaba un pago
 *      DUPLICADO real. Ahora esta función SIEMPRE devuelve un mensaje: el pago ya se
 *      guardó, así que SIEMPRE hay que avisarlo, aunque sea con el texto más genérico.
 *   2. Pago "a cuenta" (sin reserva imputada, `wasImputedToReserva=false`): mensaje fijo
 *      de saldo a favor, sin montos (no hay "deuda que bajó" que mostrar).
 *   3. Pago imputado a una reserva, SIN permiso de ver montos (`amountsVisible=false`):
 *      solo decimos A QUÉ reserva se pagó, sin números — nunca se muestran montos a
 *      alguien sin permiso de costo, ni siquiera el que él mismo acaba de escribir.
 *   4. Pago imputado a una reserva, CON permiso, deuda pendiente o saldada: "Bajó la
 *      deuda... en $X" + si queda saldo pendiente o la reserva quedó saldada.
 *   5. Bug #3 (Tanda 4, 2026-07-24) — Pago imputado a una reserva, CON permiso, que la
 *      dejó EN NEGATIVO (se pagó de más contra esa reserva puntual, `remainingBalance`
 *      negativo): en vez de mostrar "Quedan $-500 pendientes" (confuso, parece que
 *      todavía se debe), se avisa que quedó un excedente a favor nuestro con el operador.
 *
 * @param {{impact: object|null|undefined, montoImputado: number, monedaImputada: string}} datos
 * @returns {{tipo: "generico"|"a-cuenta"|"reserva-sin-monto"|"reserva"|"reserva-sobrepago", lineas: string[]}} NUNCA null — el pago ya se guardó, siempre hay algo que avisar
 */
export function construirMensajeExitoPago({ impact, montoImputado, monedaImputada }) {
    if (!impact) {
        return { tipo: "generico", lineas: ["Pago registrado."] };
    }

    if (!impact.wasImputedToReserva) {
        return {
            tipo: "a-cuenta",
            lineas: ["Pago registrado como saldo a favor. Podés usarlo en cualquier reserva de este proveedor."],
        };
    }

    const nombreReserva = impact.numeroReserva ? `la reserva ${impact.numeroReserva}` : "la reserva";
    const detalle = impact.servicioDescripcion || impact.fileName || null;
    const referenciaDestino = detalle ? `${nombreReserva} (${detalle})` : nombreReserva;

    if (!impact.amountsVisible) {
        return {
            tipo: "reserva-sin-monto",
            lineas: [`Pago registrado a ${referenciaDestino}.`],
        };
    }

    const montoTexto = formatCurrency(montoImputado, monedaImputada || impact.currency);
    const restante = Number(impact.remainingBalance ?? 0);
    const quedaSaldada = Math.abs(restante) < 0.005;
    // Bug #3: remainingBalance negativo = se pagó de más contra ESA reserva puntual.
    const quedaSobrepagada = restante < -0.005;

    return {
        tipo: quedaSobrepagada ? "reserva-sobrepago" : "reserva",
        lineas: [
            `Bajó la deuda de ${referenciaDestino} en ${montoTexto}.`,
            quedaSaldada
                ? "Esa reserva queda saldada con este operador."
                : quedaSobrepagada
                    ? `Pagaste de más: quedan ${formatCurrency(Math.abs(restante), impact.currency)} a favor nuestro con este proveedor.`
                    : `Quedan ${formatCurrency(impact.remainingBalance, impact.currency)} pendientes.`,
        ],
    };
}
