/**
 * Tests de lógica pura para los tres cambios de la "Limpieza de En viaje" (2026-06-22).
 *
 * A.1 — ServiceList: botones de escritura se ocultan cuando capabilities apagan canEditServices/canCancel.
 *        Incluye el ControlAsignacionServicio (PUT de asignaciones = escritura, mismo gate).
 * A.2 — ReservaHeader: en Traveling se ocultan "Volver atrás", "Archivar" y "Anular".
 * A.3 — ReservaStatusChips: tres ejes separados (Pago / Viaje / Factura), sin mezclar.
 *        Refinamiento review 2026-06-22: eje Viaje SOLO muestra "Vencida con deuda";
 *        "En viaje" lo dice el badge grande — NO se repite como chip.
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/soloLecturaChips.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ═══════════════════════════════════════════════════════════════════════════════
// A.1 — ServiceList: visibilidad de botones de escritura por capability
// ═══════════════════════════════════════════════════════════════════════════════

/**
 * Réplica de la lógica que decide si mostrar los botones de escritura en ServiceList.
 * El componente real usa capabilities recibidas como prop desde ReservaDetailPage.
 */
function resolverBotonesServicio({ capabilities }) {
    // Sin capabilities → degradación elegante: mostrar todo (comportamiento anterior).
    if (!capabilities) {
        return { puedeEditar: true, puedeCancelar: true };
    }
    const puedeEditar = capabilities.canEditServices?.allowed !== false;
    const puedeCancelar = capabilities.canCancel?.allowed !== false;
    return { puedeEditar, puedeCancelar };
}

/**
 * Decide si mostrar el botón destructivo (Cancelar o Borrar) de una fila de servicio.
 * Cancelar = servicio confirmado → requiere puedeCancelar.
 * Borrar    = servicio no confirmado → requiere puedeEditar.
 */
function mostrarBotonDestructivo({ esConfirmado, puedeEditar, puedeCancelar }) {
    return esConfirmado ? puedeCancelar : puedeEditar;
}

// ── Sin capabilities: todo visible (degradación elegante) ─────────────────────

test("A.1: sin capabilities → Editar y Cancelar visibles (degradación elegante)", () => {
    const { puedeEditar, puedeCancelar } = resolverBotonesServicio({ capabilities: null });
    assert.equal(puedeEditar, true);
    assert.equal(puedeCancelar, true);
});

// ── canEditServices apagado: Editar oculto ────────────────────────────────────

test("A.1: canEditServices.allowed=false → Editar oculto", () => {
    const { puedeEditar } = resolverBotonesServicio({
        capabilities: { canEditServices: { allowed: false, reason: "Reserva en viaje" } },
    });
    assert.equal(puedeEditar, false);
});

test("A.1: canEditServices.allowed=true → Editar visible", () => {
    const { puedeEditar } = resolverBotonesServicio({
        capabilities: { canEditServices: { allowed: true, reason: null } },
    });
    assert.equal(puedeEditar, true);
});

// ── canCancel apagado: botón Cancelar oculto ──────────────────────────────────

test("A.1: canCancel.allowed=false → Cancelar servicio oculto", () => {
    const { puedeCancelar } = resolverBotonesServicio({
        capabilities: { canCancel: { allowed: false, reason: "Solo lectura" } },
    });
    assert.equal(puedeCancelar, false);
});

test("A.1: canCancel.allowed=true → Cancelar servicio visible", () => {
    const { puedeCancelar } = resolverBotonesServicio({
        capabilities: { canCancel: { allowed: true, reason: null } },
    });
    assert.equal(puedeCancelar, true);
});

// ── Ambos apagados (caso Traveling real) ─────────────────────────────────────

test("A.1: en solo lectura real (Traveling) ambos apagados → Agregar/Editar/Cancelar ocultos", () => {
    const { puedeEditar, puedeCancelar } = resolverBotonesServicio({
        capabilities: {
            canEditServices: { allowed: false, reason: "En viaje" },
            canCancel: { allowed: false, reason: "En viaje" },
        },
    });
    assert.equal(puedeEditar, false);
    assert.equal(puedeCancelar, false);
});

test("A.1: servicio confirmado en solo lectura → botón Cancelar oculto", () => {
    const { puedeCancelar } = resolverBotonesServicio({
        capabilities: { canCancel: { allowed: false } },
    });
    const visible = mostrarBotonDestructivo({ esConfirmado: true, puedeEditar: false, puedeCancelar });
    assert.equal(visible, false);
});

test("A.1: servicio no confirmado en solo lectura → botón Borrar oculto", () => {
    const { puedeEditar } = resolverBotonesServicio({
        capabilities: { canEditServices: { allowed: false } },
    });
    const visible = mostrarBotonDestructivo({ esConfirmado: false, puedeEditar, puedeCancelar: false });
    assert.equal(visible, false);
});

test("A.1: estado activo normal (Confirmed con edicion habilitada) → todo visible", () => {
    const { puedeEditar, puedeCancelar } = resolverBotonesServicio({
        capabilities: {
            canEditServices: { allowed: true },
            canCancel: { allowed: true },
        },
    });
    assert.equal(puedeEditar, true);
    assert.equal(puedeCancelar, true);
});

// ── ControlAsignacionServicio: mismo gate que canEditServices ─────────────────
// El control "Para: Todos / X de N" dispara un PUT (escritura) → debe ocultarse
// en solo lectura igual que los botones Editar y Agregar.

test("A.1: ControlAsignacion en solo lectura (canEditServices=false) → oculto", () => {
    const { puedeEditar } = resolverBotonesServicio({
        capabilities: { canEditServices: { allowed: false, reason: "En viaje" } },
    });
    // El control "Para: Todos" se oculta con la misma guarda que Editar.
    assert.equal(puedeEditar, false);
});

test("A.1: ControlAsignacion con edicion habilitada → visible", () => {
    const { puedeEditar } = resolverBotonesServicio({
        capabilities: { canEditServices: { allowed: true } },
    });
    assert.equal(puedeEditar, true);
});

// ═══════════════════════════════════════════════════════════════════════════════
// A.2 — ReservaHeader: botones ocultos en Traveling
// ═══════════════════════════════════════════════════════════════════════════════

/**
 * Réplica de la lógica de visibilidad de los tres botones que se ocultan en Traveling.
 *
 * La regla es: en Traveling la reserva es inmutable por diseño.
 * - "Volver atrás" (revert): ocultado por la guarda esTraveling, aunque el backend
 *   ya devuelve allowedRevert=[].
 * - "Archivar": oculto en Traveling; en terminales (Closed/Lost/Cancelled) puede seguir.
 * - "Anular": oculto en Traveling; el backend ya bloquea canCancel.
 */
function resolverBotonesHeader({ reserva, capabilities, canCancelReserva }) {
    const esTraveling = reserva.status === "Traveling";

    // "Volver atrás" (revert): guarda defensiva en Traveling.
    const canRevertLocal = ["Budget", "InManagement", "Confirmed", "Closed", "Lost"].includes(reserva.status);
    const canRevert = !esTraveling && (
        capabilities
            ? (Array.isArray(capabilities.allowedRevert) && capabilities.allowedRevert.length > 0)
            : canRevertLocal
    );

    // "Archivar": oculto en Traveling.
    const mostrarArchivar = !esTraveling;

    // "Anular": oculto en Traveling.
    const CANCELLABLE_FALLBACK = ["InManagement", "Confirmed"];
    const cancelCapability = capabilities?.canCancel;
    const mostrarAnular = !esTraveling && canCancelReserva && (
        capabilities
            ? true
            : CANCELLABLE_FALLBACK.includes(reserva.status)
    );

    return { canRevert, mostrarArchivar, mostrarAnular };
}

// ── Traveling: los tres ocultos ───────────────────────────────────────────────

test("A.2: Traveling → 'Volver atrás' NO aparece (guarda defensiva)", () => {
    const { canRevert } = resolverBotonesHeader({
        reserva: { status: "Traveling" },
        capabilities: { allowedRevert: [] },
        canCancelReserva: true,
    });
    assert.equal(canRevert, false);
});

test("A.2: Traveling + allowedRevert=[ToSettle] (bug backend) → igual no aparece (guarda defensiva)", () => {
    // La guarda defensiva del front impide mostrar "Volver atrás" en Traveling
    // aunque el backend mandara accidentalmente allowedRevert no vacío.
    const { canRevert } = resolverBotonesHeader({
        reserva: { status: "Traveling" },
        capabilities: { allowedRevert: ["Confirmed"] },
        canCancelReserva: true,
    });
    assert.equal(canRevert, false);
});

test("A.2: Traveling → 'Archivar' NO aparece", () => {
    const { mostrarArchivar } = resolverBotonesHeader({
        reserva: { status: "Traveling" },
        capabilities: null,
        canCancelReserva: true,
    });
    assert.equal(mostrarArchivar, false);
});

test("A.2: Traveling → 'Anular' NO aparece", () => {
    const { mostrarAnular } = resolverBotonesHeader({
        reserva: { status: "Traveling" },
        capabilities: null,
        canCancelReserva: true,
    });
    assert.equal(mostrarAnular, false);
});

// ── En Confirmed: todo visible (sin cambio) ───────────────────────────────────

test("A.2: Confirmed → 'Archivar' visible (no es Traveling)", () => {
    const { mostrarArchivar } = resolverBotonesHeader({
        reserva: { status: "Confirmed" },
        capabilities: null,
        canCancelReserva: true,
    });
    assert.equal(mostrarArchivar, true);
});

test("A.2: Confirmed → 'Anular' visible (no es Traveling)", () => {
    const { mostrarAnular } = resolverBotonesHeader({
        reserva: { status: "Confirmed" },
        capabilities: null,
        canCancelReserva: true,
    });
    assert.equal(mostrarAnular, true);
});

test("A.2: Confirmed con allowedRevert=['Budget'] → 'Volver atrás' visible", () => {
    const { canRevert } = resolverBotonesHeader({
        reserva: { status: "Confirmed" },
        capabilities: { allowedRevert: ["Budget"] },
        canCancelReserva: true,
    });
    assert.equal(canRevert, true);
});

// ── Closed: Archivar visible (estado terminal donde se archiva) ───────────────

test("A.2: Closed → 'Archivar' visible", () => {
    const { mostrarArchivar } = resolverBotonesHeader({
        reserva: { status: "Closed" },
        capabilities: null,
        canCancelReserva: false,
    });
    assert.equal(mostrarArchivar, true);
});

// ═══════════════════════════════════════════════════════════════════════════════
// A.3 — ReservaStatusChips: tres ejes separados
// ═══════════════════════════════════════════════════════════════════════════════

/**
 * Réplica de la lógica de los tres ejes de ReservaStatusChips.
 * Devuelve los labels visibles en cada eje para poder testarlos.
 *
 * Refinamiento review 2026-06-22:
 * El eje Viaje SOLO emite chip cuando hasOverdueDebt === true.
 * isInProgress ya no genera chip porque el badge grande "EN VIAJE" ya lo comunica.
 *
 * Fix 2026-06-24 (H1): ahora usa collectionStatus como fuente de verdad para el eje Pago.
 *   "SinMovimientos" → "Sin movimientos" (reserva nueva sin pagos).
 *   "Saldado"        → "Pagada".
 *
 * BUG MENOR-1 fix 2026-06-24: se elimina el fallback a isFullyPaid.
 *   Antes: si collectionStatus no venía, isFullyPaid=true hacía aparecer "Pagada"
 *   aunque no hubiera cobros. Ahora: sin collectionStatus → no se muestra chip de pago.
 *
 * Q10 (2026-06-24): rótulo visible actualizado de "Sin cobros" a "Sin movimientos"
 *   (spec guia-ux-gaston.md). El data-testid "chip-pago-sin-cobros" NO cambia.
 */
function resolverChips(reserva) {
    if (!reserva) return { ejesVisibles: [], chipPago: null, chipViaje: null, ejeFactura: "Sin facturar" };

    // Eje Pago — solo lee collectionStatus (fuente de verdad del backend).
    // Sin fallback a isFullyPaid: ese fallback causaba "Pagada" falso en reservas nuevas.
    let chipPago = null;
    const collectionStatus = reserva.collectionStatus;

    if (collectionStatus === "SinMovimientos") {
        // Q10 (2026-06-24): "Sin movimientos" reemplaza "Sin cobros" como rótulo visible.
        chipPago = "Sin movimientos";
    } else if (collectionStatus === "Saldado") {
        // "Pagada" SOLO cuando el backend lo afirmó explícitamente.
        chipPago = "Pagada";
    } else if (reserva.status === "Confirmed" && reserva.isWithinUnpaidAlertWindow === true) {
        chipPago = "Debe — no viaja";
    }

    // Eje Viaje: solo para la anomalía "Vencida con deuda".
    // "En viaje" (isInProgress) ya lo comunica el badge grande — no se chip-ea.
    let chipViaje = null;
    if (reserva.hasOverdueDebt) {
        chipViaje = "Vencida con deuda";
    }

    // Eje Factura (siempre presente)
    const FACTURA_LABELS = {
        NotInvoiced: "Sin facturar",
        PartiallyInvoiced: "Facturada en parte",
        FullyInvoiced: "Facturada total",
    };
    const ejeFactura = FACTURA_LABELS[reserva.invoicingStatus] || "Sin facturar";

    return { chipPago, chipViaje, ejeFactura };
}

// ── Caso objetivo de la spec: En viaje, pagada, facturada total ───────────────
// Refinamiento review 2026-06-22: "En viaje" lo dice el badge grande.
// Una reserva en viaje pagada y facturada muestra solo Pago:Pagada + Factura:Facturada total.

test("A.3: Traveling + pagada + facturada total → Pago:Pagada · sin chip Viaje · Factura:Facturada total", () => {
    const { chipPago, chipViaje, ejeFactura } = resolverChips({
        status: "Traveling",
        collectionStatus: "Saldado",
        isInProgress: true,
        hasOverdueDebt: false,
        invoicingStatus: "FullyInvoiced",
    });
    assert.equal(chipPago, "Pagada");
    // Sin chip de viaje: el badge grande ya dice "EN VIAJE", repetirlo es ruido.
    assert.equal(chipViaje, null);
    assert.equal(ejeFactura, "Facturada total");
});

// ── isInProgress ya NO genera chip en el eje Viaje ──────────────────────────

test("A.3: Traveling con isInProgress=true → chipViaje=null (badge grande lo comunica)", () => {
    const { chipViaje } = resolverChips({
        status: "Traveling",
        collectionStatus: "Saldado",
        isInProgress: true,
        hasOverdueDebt: false,
        invoicingStatus: "NotInvoiced",
    });
    // El eje Viaje queda vacío cuando el viaje está en curso pero sin deuda vencida.
    assert.equal(chipViaje, null);
});

// ── "Pagada" aparece cuando collectionStatus===Saldado (en cualquier estado) ──

test("A.3: collectionStatus=Saldado en Traveling → chipPago=Pagada", () => {
    const { chipPago } = resolverChips({
        status: "Traveling",
        collectionStatus: "Saldado",
        isInProgress: true,
        invoicingStatus: "NotInvoiced",
    });
    assert.equal(chipPago, "Pagada");
});

test("A.3: collectionStatus=Saldado en Closed → chipPago=Pagada", () => {
    const { chipPago } = resolverChips({
        status: "Closed",
        collectionStatus: "Saldado",
        isInProgress: false,
        invoicingStatus: "FullyInvoiced",
    });
    assert.equal(chipPago, "Pagada");
});

test("A.3: collectionStatus=Saldado en Confirmed → chipPago=Pagada", () => {
    const { chipPago } = resolverChips({
        status: "Confirmed",
        collectionStatus: "Saldado",
        isInProgress: false,
        invoicingStatus: "NotInvoiced",
    });
    assert.equal(chipPago, "Pagada");
});

// ── "Debe — no viaja" solo en Confirmed dentro de la ventana ─────────────────

test("A.3: Confirmed + no pagada + dentro de ventana → chipPago=Debe — no viaja", () => {
    const { chipPago } = resolverChips({
        status: "Confirmed",
        isFullyPaid: false,
        isWithinUnpaidAlertWindow: true,
        invoicingStatus: "NotInvoiced",
    });
    assert.equal(chipPago, "Debe — no viaja");
});

test("A.3: Confirmed + no pagada + FUERA de ventana → chipPago=null (no se muestra)", () => {
    const { chipPago } = resolverChips({
        status: "Confirmed",
        isFullyPaid: false,
        isWithinUnpaidAlertWindow: false,
        invoicingStatus: "NotInvoiced",
    });
    assert.equal(chipPago, null);
});

test("A.3: InManagement + no pagada → chipPago=null (solo en Confirmed)", () => {
    const { chipPago } = resolverChips({
        status: "InManagement",
        isFullyPaid: false,
        isWithinUnpaidAlertWindow: true,
        invoicingStatus: "NotInvoiced",
    });
    assert.equal(chipPago, null);
});

// ── Eje Viaje: "Vencida con deuda" es más urgente que "En viaje" ─────────────

test("A.3: hasOverdueDebt=true → chipViaje=Vencida con deuda (no En viaje aunque isInProgress=true)", () => {
    const { chipViaje } = resolverChips({
        status: "Traveling",
        isFullyPaid: false,
        isInProgress: true,
        hasOverdueDebt: true,
        invoicingStatus: "NotInvoiced",
    });
    assert.equal(chipViaje, "Vencida con deuda");
});

test("A.3: isInProgress=false y hasOverdueDebt=false → chipViaje=null (eje Viaje no aparece)", () => {
    const { chipViaje } = resolverChips({
        status: "Confirmed",
        isFullyPaid: true,
        isInProgress: false,
        hasOverdueDebt: false,
        invoicingStatus: "NotInvoiced",
    });
    assert.equal(chipViaje, null);
});

// ── Eje Factura: siempre visible ──────────────────────────────────────────────

test("A.3: eje Factura siempre presente, incluso en Quotation sin factura", () => {
    const { ejeFactura } = resolverChips({
        status: "Quotation",
        isFullyPaid: false,
        invoicingStatus: "NotInvoiced",
    });
    assert.equal(ejeFactura, "Sin facturar");
});

test("A.3: invoicingStatus=PartiallyInvoiced → Facturada en parte", () => {
    const { ejeFactura } = resolverChips({
        status: "Confirmed",
        isFullyPaid: true,
        invoicingStatus: "PartiallyInvoiced",
    });
    assert.equal(ejeFactura, "Facturada en parte");
});

test("A.3: invoicingStatus=FullyInvoiced → Facturada total", () => {
    const { ejeFactura } = resolverChips({
        status: "Traveling",
        isFullyPaid: true,
        invoicingStatus: "FullyInvoiced",
    });
    assert.equal(ejeFactura, "Facturada total");
});

test("A.3: invoicingStatus ausente → Sin facturar (fallback)", () => {
    const { ejeFactura } = resolverChips({
        status: "Budget",
        isFullyPaid: false,
    });
    assert.equal(ejeFactura, "Sin facturar");
});

// ── Caso sin reserva: degradación elegante ────────────────────────────────────

test("A.3: reserva=null → chips vacíos sin crash", () => {
    const { chipPago, chipViaje, ejeFactura } = resolverChips(null);
    assert.equal(chipPago, null);
    assert.equal(chipViaje, null);
    assert.equal(ejeFactura, "Sin facturar");
});

// ═══════════════════════════════════════════════════════════════════════════════
// A.4 — Fix H1 2026-06-24 + Q10: collectionStatus="SinMovimientos" → chip "Sin movimientos"
//
// Antes del fix: una reserva nueva (balance=0, sin cobros) llegaba al front con
// collectionStatus="Saldado" (bug del backend), y el chip decía "Pagada" cuando
// no se había cobrado nada. Ahora el backend distingue los dos casos:
//   "Saldado"        = el cliente pagó y no queda deuda.
//   "SinMovimientos" = la reserva no tiene ningún cargo ni cobro todavía.
//
// Q10 (2026-06-24): rótulo visible actualizado de "Sin cobros" → "Sin movimientos"
// (spec guia-ux-gaston.md). El data-testid "chip-pago-sin-cobros" NO cambia.
// ═══════════════════════════════════════════════════════════════════════════════

test("A.4: collectionStatus=SinMovimientos → chipPago='Sin movimientos' (no 'Pagada')", () => {
    // Caso exacto del bug: reserva nueva en InManagement, sin cobros, balance=0.
    // El backend ya corrigió: ahora envía SinMovimientos en vez de Saldado.
    const { chipPago } = resolverChips({
        status: "InManagement",
        collectionStatus: "SinMovimientos",
        isFullyPaid: false,
        invoicingStatus: "NotInvoiced",
    });
    // Q10: rótulo visible es "Sin movimientos", no "Sin cobros".
    assert.equal(chipPago, "Sin movimientos");
});

test("A.4: collectionStatus=SinMovimientos en Quotation → chipPago='Sin movimientos'", () => {
    const { chipPago } = resolverChips({
        status: "Quotation",
        collectionStatus: "SinMovimientos",
        isFullyPaid: false,
        invoicingStatus: "NotInvoiced",
    });
    assert.equal(chipPago, "Sin movimientos");
});

test("A.4: collectionStatus=SinMovimientos NO muestra 'Pagada' aunque isFullyPaid sea true", () => {
    // Caso defensivo: si el backend manda SinMovimientos, el SinMovimientos gana
    // aunque isFullyPaid sea true por error. La fuente de verdad es collectionStatus.
    const { chipPago } = resolverChips({
        status: "InManagement",
        collectionStatus: "SinMovimientos",
        isFullyPaid: true, // inconsistente, pero collectionStatus gana
        invoicingStatus: "NotInvoiced",
    });
    // Q10: el rótulo visible es "Sin movimientos", no "Sin cobros".
    assert.equal(chipPago, "Sin movimientos");
});

test("A.4: collectionStatus=Saldado → chipPago='Pagada' (distinción clara de SinMovimientos)", () => {
    const { chipPago } = resolverChips({
        status: "Confirmed",
        collectionStatus: "Saldado",
        invoicingStatus: "NotInvoiced",
    });
    assert.equal(chipPago, "Pagada");
});

test("A.4: sin collectionStatus + isFullyPaid=true → chipPago=null (BUG MENOR-1 fix 2026-06-24)", () => {
    // BUG MENOR-1 fix: antes el fallback a isFullyPaid hacía aparecer "Pagada" aunque
    // no hubiera ningún cobro registrado. Ahora sin collectionStatus → no se muestra chip.
    // El caso real (DTO moderno) siempre envía collectionStatus; el caso DTO viejo
    // es mejor no mostrar nada que mostrar algo incorrecto.
    const { chipPago } = resolverChips({
        status: "Confirmed",
        // collectionStatus ausente (DTO viejo o bug de integración)
        isFullyPaid: true,
        invoicingStatus: "NotInvoiced",
    });
    assert.equal(chipPago, null, "Sin collectionStatus no debe aparecer 'Pagada' aunque isFullyPaid sea true");
});

test("A.4: sin collectionStatus + isFullyPaid=false → chipPago=null (sin movimientos sin collectionStatus)", () => {
    // Sin collectionStatus y sin deuda dentro de ventana: no mostramos nada.
    const { chipPago } = resolverChips({
        status: "InManagement",
        // collectionStatus ausente (DTO viejo)
        isFullyPaid: false,
        invoicingStatus: "NotInvoiced",
    });
    assert.equal(chipPago, null);
});

test("A.4: collectionStatus=ConDeuda fuera de ventana de aviso → chipPago=null (solo avisa dentro de ventana)", () => {
    // ConDeuda sin ventana de aviso: el chip "Debe" solo aparece en Confirmed + dentro de ventana.
    // Si no, no mostramos nada para no alarmar en estados donde no aplica la urgencia.
    const { chipPago } = resolverChips({
        status: "InManagement",
        collectionStatus: "ConDeuda",
        isFullyPaid: false,
        isWithinUnpaidAlertWindow: false,
        invoicingStatus: "NotInvoiced",
    });
    assert.equal(chipPago, null);
});

// ═══════════════════════════════════════════════════════════════════════════════
// BUG IMP-3 fix 2026-06-24: Editar/Eliminar cobro se rigen por canEditOrDeletePayment
// (capacidad real del backend), no por el helper "congelado" del frontend.
//
// Antes: en Closed (Finalizada), congelado=false → aparecían Editar/Eliminar aunque el
//        backend los rechazara (canEditOrDeletePayment.allowed === false en estados terminales).
// Ahora: canEditarEliminar = capabilities.canEditOrDeletePayment.allowed.
//        El backend ya sabe en qué estados se puede editar/eliminar un cobro.
// ═══════════════════════════════════════════════════════════════════════════════

/**
 * Réplica de la lógica de PaymentReceiptActions para el par Editar/Eliminar cobro.
 * canEditarEliminar viene directamente de la capacidad del backend.
 */
function calcularCobroEditable({ canEditarEliminar, reciboAnulado }) {
    // El backend decide si la acción está disponible en este estado.
    // Guard extra: si el recibo ya fue anulado, no tiene sentido editar el cobro.
    return Boolean(canEditarEliminar) && !reciboAnulado;
}

test("IMP-3: canEditOrDeletePayment=true + sin recibo anulado → Editar y Eliminar visibles", () => {
    const editable = calcularCobroEditable({ canEditarEliminar: true, reciboAnulado: false });
    assert.equal(editable, true);
});

test("IMP-3: canEditOrDeletePayment=false (Closed/terminal) → Editar y Eliminar OCULTOS", () => {
    // Closed: el backend devuelve canEditOrDeletePayment.allowed=false.
    // Antes del fix este caso mostraba los botones (congelado=false en Closed).
    const editable = calcularCobroEditable({ canEditarEliminar: false, reciboAnulado: false });
    assert.equal(editable, false, "En Closed el backend rechaza editar/eliminar cobros");
});

test("IMP-3: canEditOrDeletePayment=true + recibo anulado → Editar y Eliminar OCULTOS (guard de recibo)", () => {
    // Aunque el backend permita editar, un recibo anulado ya fue procesado formalmente.
    const editable = calcularCobroEditable({ canEditarEliminar: true, reciboAnulado: true });
    assert.equal(editable, false, "Un recibo anulado no se puede editar aunque el backend lo permita");
});

test("IMP-3: canEditOrDeletePayment undefined (DTO sin capability) → Editar y Eliminar OCULTOS (seguro por defecto)", () => {
    // Si el DTO no trae la capacidad, Boolean(undefined) = false → botones ocultos.
    // Es mejor no mostrar que mostrar un botón que el server va a rechazar.
    const editable = calcularCobroEditable({ canEditarEliminar: undefined, reciboAnulado: false });
    assert.equal(editable, false, "Sin capability del backend se asume false por seguridad");
});

// ═══════════════════════════════════════════════════════════════════════════════
// BUG IMP-4 fix 2026-06-24: FullyInvoiced NO bloquea las acciones de COBRO.
//
// Antes: esEstadoCongelado incluía FullyInvoiced → en una reserva Confirmed+FullyInvoiced
//        los botones de emitir/anular recibo de cobro se ocultaban, aunque el cliente
//        pudiera hacer un pago pendiente.
// Ahora: esCongeladoParaRecibos (nuevo helper) NO incluye FullyInvoiced.
//        FullyInvoiced sigue en esEstadoCongelado para vouchers y documentos.
//        Las acciones de cobro se rigen por la capacidad canEditOrDeletePayment.
// ═══════════════════════════════════════════════════════════════════════════════

/**
 * Réplica de esCongeladoParaRecibos (sin FullyInvoiced).
 * Controla si se puede emitir o anular el recibo de un cobro.
 */
function esCongeladoParaRecibos(reserva) {
    if (!reserva) return false;
    return (
        reserva.status === "Traveling" ||
        reserva.status === "Lost" ||
        reserva.status === "Cancelled" ||
        reserva.status === "PendingOperatorRefund"
    );
}

test("IMP-4: Confirmed + FullyInvoiced → esCongeladoParaRecibos = false (cobro no bloqueado)", () => {
    // BUG IMP-4: antes FullyInvoiced bloqueaba los recibos. Ahora no: facturación y
    // cobranza son ejes separados (ADR-037). Si hay un cobro pendiente, se puede registrar.
    const result = esCongeladoParaRecibos({ status: "Confirmed", invoicingStatus: "FullyInvoiced" });
    assert.equal(result, false, "FullyInvoiced no debe bloquear las acciones de cobro");
});

test("IMP-4: Closed + FullyInvoiced → esCongeladoParaRecibos = false (Closed tampoco bloquea recibos)", () => {
    // Closed (Finalizada): el bloqueo de editar/eliminar viene de canEditOrDeletePayment,
    // no de congeladoParaRecibos. Emitir un recibo de un cobro reciente sigue siendo válido.
    const result = esCongeladoParaRecibos({ status: "Closed", invoicingStatus: "FullyInvoiced" });
    assert.equal(result, false);
});

test("IMP-4: Traveling → esCongeladoParaRecibos = true (viaje en curso)", () => {
    const result = esCongeladoParaRecibos({ status: "Traveling", invoicingStatus: "NotInvoiced" });
    assert.equal(result, true);
});

test("IMP-4: Lost → esCongeladoParaRecibos = true", () => {
    const result = esCongeladoParaRecibos({ status: "Lost", invoicingStatus: "NotInvoiced" });
    assert.equal(result, true);
});

test("IMP-4: Cancelled → esCongeladoParaRecibos = true", () => {
    const result = esCongeladoParaRecibos({ status: "Cancelled", invoicingStatus: "NotInvoiced" });
    assert.equal(result, true);
});

// ═══════════════════════════════════════════════════════════════════════════════
// BUG IMP-4 / BUG 3: balance negativo = A favor (no deuda roja)
// EstadoCuentaResumen: si balance < 0 → rótulo "A favor" en verde, monto positivo.
// ═══════════════════════════════════════════════════════════════════════════════

/**
 * Réplica de la lógica de EjeBalanceMono / ColumnaBalanceMulti.
 * Decide el rótulo y el color según el signo del balance.
 */
function resolverDisplayBalance(balance) {
    const valor = balance ?? 0;
    if (valor < 0) {
        return { rotulo: "A favor", monto: Math.abs(valor), esAFavor: true };
    }
    return {
        rotulo: "Saldo a cobrar",
        monto: valor,
        esAFavor: false,
    };
}

test("BUG3: balance negativo → rótulo 'A favor', monto en positivo (no deuda roja)", () => {
    // Caso del bug: balance=-500 aparecía como "Saldo a cobrar: -$500" en rojo.
    const display = resolverDisplayBalance(-500);
    assert.equal(display.rotulo, "A favor", "Balance negativo debe rotularse A favor");
    assert.equal(display.monto, 500, "El monto debe ser positivo (Math.abs)");
    assert.equal(display.esAFavor, true);
});

test("BUG3: balance positivo → rótulo 'Saldo a cobrar' (cliente debe plata)", () => {
    const display = resolverDisplayBalance(1200);
    assert.equal(display.rotulo, "Saldo a cobrar");
    assert.equal(display.monto, 1200);
    assert.equal(display.esAFavor, false);
});

test("BUG3: balance cero → rótulo 'Saldo a cobrar', monto $0 (neutro)", () => {
    const display = resolverDisplayBalance(0);
    assert.equal(display.rotulo, "Saldo a cobrar");
    assert.equal(display.monto, 0);
    assert.equal(display.esAFavor, false);
});

test("BUG3: balance null → trata como 0, rótulo 'Saldo a cobrar'", () => {
    // Degradación elegante si el backend no envía el campo.
    const display = resolverDisplayBalance(null);
    assert.equal(display.monto, 0);
    assert.equal(display.esAFavor, false);
});

// ═══════════════════════════════════════════════════════════════════════════════
// BUG MENOR-3 fix 2026-06-24: ReservaTable ya no asume "Saldado" por defecto.
// El else final antes pintaba "Saldado" verde para cualquier collectionStatus
// desconocido (null, SaldoAFavor, etc.). Ahora cada caso es explícito.
// ═══════════════════════════════════════════════════════════════════════════════

/**
 * Réplica de la lógica de la columna Finanzas en ReservaTable.
 * Devuelve el rótulo del estado de cobro para la fila de la tabla.
 */
function resolverRotuloTabla({ balance, collectionStatus }) {
    if (balance > 0) {
        return "Debe";
    }
    if (collectionStatus === "SinMovimientos") {
        return "Sin movimientos";
    }
    if (collectionStatus === "Saldado") {
        return "Saldado";
    }
    if (collectionStatus === "SaldoAFavor") {
        return "A favor";
    }
    // Desconocido o DTO sin collectionStatus: neutro, no asumir "Saldado".
    return "Sin movimientos";
}

test("MENOR-3: balance > 0 → 'Debe' (cliente debe plata)", () => {
    const rotulo = resolverRotuloTabla({ balance: 500, collectionStatus: "ConDeuda" });
    assert.equal(rotulo, "Debe");
});

test("MENOR-3: collectionStatus=Saldado → 'Saldado' verde (solo cuando el backend lo dice)", () => {
    const rotulo = resolverRotuloTabla({ balance: 0, collectionStatus: "Saldado" });
    assert.equal(rotulo, "Saldado");
});

test("MENOR-3: collectionStatus=SaldoAFavor → 'A favor' (tiene su propio rótulo)", () => {
    // Antes del fix: caía en el else y mostraba "Saldado" verde — incorrecto.
    const rotulo = resolverRotuloTabla({ balance: 0, collectionStatus: "SaldoAFavor" });
    assert.equal(rotulo, "A favor", "SaldoAFavor debe mostrar 'A favor', no 'Saldado'");
});

test("MENOR-3: collectionStatus=SinMovimientos → 'Sin movimientos' (reserva nueva)", () => {
    const rotulo = resolverRotuloTabla({ balance: 0, collectionStatus: "SinMovimientos" });
    assert.equal(rotulo, "Sin movimientos");
});

test("MENOR-3: collectionStatus ausente (null) → 'Sin movimientos' (neutro, nunca 'Saldado' falso)", () => {
    // BUG MENOR-3 fix: antes el else asumía "Saldado" para null → incorrecto.
    const rotulo = resolverRotuloTabla({ balance: 0, collectionStatus: null });
    assert.equal(rotulo, "Sin movimientos", "Sin collectionStatus no debe asumir Saldado");
});

test("MENOR-3: collectionStatus desconocido (string raro) → 'Sin movimientos' (neutro)", () => {
    const rotulo = resolverRotuloTabla({ balance: 0, collectionStatus: "ValorNuevoDelBackend" });
    assert.equal(rotulo, "Sin movimientos", "Valor desconocido no debe asumir Saldado verde");
});
