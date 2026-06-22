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
 */
function resolverChips(reserva) {
    if (!reserva) return { ejesVisibles: [], chipPago: null, chipViaje: null, ejeFactura: "Sin facturar" };

    // Eje Pago
    let chipPago = null;
    if (reserva.isFullyPaid) {
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
        isFullyPaid: true,
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
        isFullyPaid: true,
        isInProgress: true,
        hasOverdueDebt: false,
        invoicingStatus: "NotInvoiced",
    });
    // El eje Viaje queda vacío cuando el viaje está en curso pero sin deuda vencida.
    assert.equal(chipViaje, null);
});

// ── "Pagada" ahora aparece en cualquier estado (no solo Confirmed) ────────────

test("A.3: isFullyPaid=true en Traveling → chipPago=Pagada", () => {
    const { chipPago } = resolverChips({
        status: "Traveling",
        isFullyPaid: true,
        isInProgress: true,
        invoicingStatus: "NotInvoiced",
    });
    assert.equal(chipPago, "Pagada");
});

test("A.3: isFullyPaid=true en Closed → chipPago=Pagada", () => {
    const { chipPago } = resolverChips({
        status: "Closed",
        isFullyPaid: true,
        isInProgress: false,
        invoicingStatus: "FullyInvoiced",
    });
    assert.equal(chipPago, "Pagada");
});

test("A.3: isFullyPaid=true en Confirmed → chipPago=Pagada (como antes)", () => {
    const { chipPago } = resolverChips({
        status: "Confirmed",
        isFullyPaid: true,
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
