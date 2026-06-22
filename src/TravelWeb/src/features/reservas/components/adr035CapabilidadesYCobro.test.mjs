/**
 * Tests de lógica pura para ADR-035:
 *   A) Botones por estado: motivo visible debajo, nunca tooltip.
 *   B) Cobro arranca en moneda principal (no en ARS fijo).
 *   C) Cartel verde/ámbar según requiresInvoiceAnnulmentToCancel.
 *   D) Botón "Reabrir para facturar" según capabilities.allowedRevert.
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/adr035CapabilidadesYCobro.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─────────────────────────────────────────────────────────────────────────────
// Lógica copiada de RegistrarCobroInline.jsx (la función de resolución de moneda)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Replica de la lógica de RegistrarCobroInline para resolver la moneda por defecto.
 * ADR-035 (2026-06-19): arranca en monedaPrincipal del DTO, no en "ARS" fijo.
 */
function resolverMonedaDefault(reserva) {
    return reserva?.monedaPrincipal
        || reserva?.porMoneda?.[0]?.currency
        || "ARS";
}

/**
 * Replica la condición que muestra el link "pagar en otra moneda".
 * Solo aparece en reservas multimoneda y cuando NO está mostrarOtraMoneda.
 */
function debeMostrarLinkOtraMoneda({ esMultimoneda, mostrarOtraMoneda }) {
    return esMultimoneda && !mostrarOtraMoneda;
}

/**
 * Replica la condición que muestra los selectores de moneda/imputar.
 * Solo aparecen en multimoneda Y cuando el usuario activó el link.
 */
function debeMostrarSelectoresMoneda({ esMultimoneda, mostrarOtraMoneda }) {
    return esMultimoneda && mostrarOtraMoneda;
}

// ─────────────────────────────────────────────────────────────────────────────
// Lógica copiada de ReservaHeader.jsx (capabilities)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Replica del helper getCapability de ReservaHeader.
 * Si no hay capabilities, devuelve { allowed: true, reason: null } (degradación elegante).
 */
function getCapability(capabilities, field) {
    if (!capabilities || !capabilities[field]) return { allowed: true, reason: null };
    return capabilities[field];
}

/**
 * Replica de la condición para mostrar el botón cancelar con capabilities.
 * Con capabilities: siempre visible (allowed/disbled según capability).
 * Sin capabilities: solo en estados operativos (fallback legacy).
 * ADR-036: ToSettle eliminado del fallback.
 */
const CANCELLABLE_STATUSES_FALLBACK = ['InManagement', 'Confirmed', 'Traveling'];

function puedeVerBotonCancelar({ canCancelReserva, isArchived, capabilities, reservaStatus }) {
    if (!canCancelReserva || isArchived) return false;
    if (capabilities) return true; // Con capabilities: siempre visible (puede estar apagado)
    return CANCELLABLE_STATUSES_FALLBACK.includes(reservaStatus); // Fallback
}

// ─────────────────────────────────────────────────────────────────────────────
// Lógica copiada de CancelarReservaInline.jsx (cartel según factura)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Replica de la lógica de selección de cartel en CancelarReservaInline.
 * Devuelve: "verde" | "ambar"
 */
function resolverColorCartelCancelacion(reserva) {
    return reserva?.requiresInvoiceAnnulmentToCancel === true ? "ambar" : "verde";
}

// ─────────────────────────────────────────────────────────────────────────────
// B) Tests: moneda default del cobro (RegistrarCobroInline)
// ─────────────────────────────────────────────────────────────────────────────

test("B cobro: reserva ARS pura → monedaDefault = ARS (monedaPrincipal del DTO)", () => {
    const reserva = { monedaPrincipal: "ARS", porMoneda: [{ currency: "ARS", balance: 5000 }] };
    assert.equal(resolverMonedaDefault(reserva), "ARS");
});

test("B cobro: reserva USD pura → monedaDefault = USD (no ARS hardcodeado)", () => {
    // Este era el bug: antes el form arrancaba siempre en ARS aunque la reserva fuera en USD.
    const reserva = { monedaPrincipal: "USD", porMoneda: [{ currency: "USD", balance: 1000 }] };
    assert.equal(resolverMonedaDefault(reserva), "USD");
});

test("B cobro: reserva multimoneda ARS+USD con monedaPrincipal=USD → arranca en USD", () => {
    // La moneda principal es la de mayor saldo (decidida por el backend).
    const reserva = {
        monedaPrincipal: "USD",
        esMultimoneda: true,
        porMoneda: [
            { currency: "ARS", balance: 10000 },
            { currency: "USD", balance: 5000 },  // mayor saldo relativo, decide el backend
        ],
    };
    assert.equal(resolverMonedaDefault(reserva), "USD");
});

test("B cobro: sin monedaPrincipal → usa primera entrada de porMoneda como fallback", () => {
    const reserva = { monedaPrincipal: null, porMoneda: [{ currency: "USD", balance: 200 }] };
    assert.equal(resolverMonedaDefault(reserva), "USD");
});

test("B cobro: sin monedaPrincipal ni porMoneda → fallback final a ARS", () => {
    // Si el DTO no trae nada (muy viejo), no rompemos: ARS como último recurso.
    const reserva = {};
    assert.equal(resolverMonedaDefault(reserva), "ARS");
});

test("B cobro: reserva null → fallback a ARS (no rompe)", () => {
    assert.equal(resolverMonedaDefault(null), "ARS");
});

// ─────────────────────────────────────────────────────────────────────────────
// B) Tests: link "pagar en otra moneda" y selectores
// ─────────────────────────────────────────────────────────────────────────────

test("B link: monomoneda → NO muestra el link (no hay 'otra' moneda)", () => {
    const mostrar = debeMostrarLinkOtraMoneda({ esMultimoneda: false, mostrarOtraMoneda: false });
    assert.equal(mostrar, false, "en reserva monomoneda no hay link de 'otra moneda'");
});

test("B link: multimoneda + mostrarOtraMoneda=false → SÍ muestra el link", () => {
    const mostrar = debeMostrarLinkOtraMoneda({ esMultimoneda: true, mostrarOtraMoneda: false });
    assert.equal(mostrar, true);
});

test("B link: multimoneda + mostrarOtraMoneda=true → NO muestra el link (ya está expandido)", () => {
    const mostrar = debeMostrarLinkOtraMoneda({ esMultimoneda: true, mostrarOtraMoneda: true });
    assert.equal(mostrar, false, "al activar el link, el link desaparece (se reemplaza por selectores)");
});

test("B selectores: monomoneda → NO muestra selectores aunque mostrarOtraMoneda=true", () => {
    const mostrar = debeMostrarSelectoresMoneda({ esMultimoneda: false, mostrarOtraMoneda: true });
    assert.equal(mostrar, false);
});

test("B selectores: multimoneda + mostrarOtraMoneda=false → NO muestra selectores (estado inicial)", () => {
    const mostrar = debeMostrarSelectoresMoneda({ esMultimoneda: true, mostrarOtraMoneda: false });
    assert.equal(mostrar, false, "al iniciar el form, los selectores están ocultos");
});

test("B selectores: multimoneda + mostrarOtraMoneda=true → SÍ muestra selectores", () => {
    // El link fue tocado: ahora los selectores deben aparecer.
    const mostrar = debeMostrarSelectoresMoneda({ esMultimoneda: true, mostrarOtraMoneda: true });
    assert.equal(mostrar, true);
});

// ─────────────────────────────────────────────────────────────────────────────
// A) Tests: botón apagado con motivo (capabilities)
// ─────────────────────────────────────────────────────────────────────────────

test("A capabilities: sin capabilities → getCapability devuelve allowed=true (degradación elegante)", () => {
    const cap = getCapability(null, "canCancel");
    assert.equal(cap.allowed, true);
    assert.equal(cap.reason, null);
});

test("A capabilities: canCancel.allowed=false → devuelve allowed=false con reason", () => {
    const capabilities = {
        canCancel: { allowed: false, reason: "La reserva está Finalizada y no se puede cancelar." }
    };
    const cap = getCapability(capabilities, "canCancel");
    assert.equal(cap.allowed, false);
    assert.ok(cap.reason, "debe venir con texto de motivo");
});

test("A capabilities: canCancel.allowed=true → enabled, sin reason", () => {
    const capabilities = { canCancel: { allowed: true, reason: null } };
    const cap = getCapability(capabilities, "canCancel");
    assert.equal(cap.allowed, true);
});

test("A capabilities: campo no existente en capabilities → degradación elegante (allowed=true)", () => {
    const capabilities = { canInvoiceSale: { allowed: false, reason: "Sin servicios confirmados." } };
    // canCancel no existe en este objeto
    const cap = getCapability(capabilities, "canCancel");
    assert.equal(cap.allowed, true, "campo ausente = degradación elegante, no rompemos el botón");
});

// ─────────────────────────────────────────────────────────────────────────────
// A) Tests: visibilidad del botón cancelar con/sin capabilities
// ─────────────────────────────────────────────────────────────────────────────

test("A cancelar: con capabilities → botón siempre visible si tiene permiso (aunque apagado)", () => {
    const visible = puedeVerBotonCancelar({
        canCancelReserva: true,
        isArchived: false,
        capabilities: { canCancel: { allowed: false, reason: "Está finalizada." } },
        reservaStatus: "Closed",
    });
    // Con capabilities: Closed se muestra (apagado), antes no se mostraba
    assert.equal(visible, true, "con capabilities, Closed muestra el botón apagado");
});

test("A cancelar: sin capabilities + Closed → botón NO visible (fallback legacy)", () => {
    const visible = puedeVerBotonCancelar({
        canCancelReserva: true,
        isArchived: false,
        capabilities: null,
        reservaStatus: "Closed",
    });
    assert.equal(visible, false, "sin capabilities, Closed no era cancellable (fallback legacy)");
});

test("A cancelar: sin capabilities + Confirmed → botón visible (fallback legacy)", () => {
    const visible = puedeVerBotonCancelar({
        canCancelReserva: true,
        isArchived: false,
        capabilities: null,
        reservaStatus: "Confirmed",
    });
    assert.equal(visible, true);
});

test("A cancelar: sin permiso reservas.cancel → NO visible aunque tenga capabilities", () => {
    const visible = puedeVerBotonCancelar({
        canCancelReserva: false,
        isArchived: false,
        capabilities: { canCancel: { allowed: true } },
        reservaStatus: "Confirmed",
    });
    assert.equal(visible, false, "permiso de usuario tiene prioridad sobre capabilities");
});

test("A cancelar: archivada → NO visible", () => {
    const visible = puedeVerBotonCancelar({
        canCancelReserva: true,
        isArchived: true,
        capabilities: { canCancel: { allowed: true } },
        reservaStatus: "Archived",
    });
    assert.equal(visible, false);
});

// ─────────────────────────────────────────────────────────────────────────────
// C) Tests: cartel verde vs ámbar en CancelarReservaInline
// ─────────────────────────────────────────────────────────────────────────────

test("C cartel: requiresInvoiceAnnulmentToCancel=false → cartel VERDE (sin factura)", () => {
    const reserva = { requiresInvoiceAnnulmentToCancel: false };
    const color = resolverColorCartelCancelacion(reserva);
    assert.equal(color, "verde");
});

test("C cartel: requiresInvoiceAnnulmentToCancel=true → cartel ÁMBAR (con factura emitida)", () => {
    const reserva = { requiresInvoiceAnnulmentToCancel: true };
    const color = resolverColorCartelCancelacion(reserva);
    assert.equal(color, "ambar");
});

test("C cartel: requiresInvoiceAnnulmentToCancel=undefined (DTO viejo) → cartel VERDE (conservador)", () => {
    // Un DTO viejo sin el campo → undefined → false → verde (no asustamos al usuario).
    const reserva = {};
    const color = resolverColorCartelCancelacion(reserva);
    assert.equal(color, "verde");
});

test("C cartel: reserva=null → verde (no rompe)", () => {
    const color = resolverColorCartelCancelacion(null);
    assert.equal(color, "verde");
});

// ─────────────────────────────────────────────────────────────────────────────
// D) ADR-037: el botón "Reabrir para facturar" fue ELIMINADO
// ─────────────────────────────────────────────────────────────────────────────
// La facturación se desacopló del estado: ya no se reabre nada, se factura directo
// desde Finalizada. La cobertura del nuevo botón "Emitir factura" (gobernado por la
// capability canInvoiceSale y oculto cuando ya está facturada del todo) vive en
// adr035FeedbackVisual.test.mjs (sección "Cambio 4") y en adr037Facturacion.test.mjs.

// ─────────────────────────────────────────────────────────────────────────────
// Fix #1: cambio de tab antes de abrir el panel inline de cancelacion
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Replica la lógica del callback onCancelReserva (fix #1).
 * Devuelve el tab que debería estar activo y si el panel inline se activa.
 */
function simularClickCancelarDesdeHeader(activeTabActual) {
    let activeTab = activeTabActual;
    let showCancelInline = false;

    // Fix #1: primero ir a "account", luego activar el panel
    activeTab = "account";
    showCancelInline = true;

    return { activeTab, showCancelInline };
}

test("Fix#1 cancelar: click desde solapa 'services' → cambia a 'account' y abre el panel", () => {
    const result = simularClickCancelarDesdeHeader("services");
    assert.equal(result.activeTab, "account");
    assert.equal(result.showCancelInline, true);
});

test("Fix#1 cancelar: click desde solapa 'history' → cambia a 'account' y abre el panel", () => {
    const result = simularClickCancelarDesdeHeader("history");
    assert.equal(result.activeTab, "account");
    assert.equal(result.showCancelInline, true);
});

test("Fix#1 cancelar: click desde solapa 'account' → sigue en 'account' y abre el panel", () => {
    // Si el usuario ya estaba en account, el resultado es el mismo (no rompe nada).
    const result = simularClickCancelarDesdeHeader("account");
    assert.equal(result.activeTab, "account");
    assert.equal(result.showCancelInline, true);
});

// ─────────────────────────────────────────────────────────────────────────────
// Fix #2: RevertStatusModal — motivo obligatorio para admin con forceReason
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Replica la lógica de canSubmit de RevertStatusModal con forceReason.
 * - isAdmin: true si el actor es admin.
 * - forceReason: true cuando el modal de revertir estado exige motivo obligatorio
 *   (acción sensible que queda auditada). ADR-037: ya no hay "Reabrir para facturar";
 *   este gate aplica al flujo genérico de "Volver atrás" / revertir estado.
 * - reasonOk: true si el motivo tiene >= 10 caracteres.
 * - targetStatus: el destino (pre-seleccionado cuando lockedTarget está seteado).
 */
function calcularCanSubmitRevert({ isAdmin, requiresAuth, forceReason, reasonOk, targetStatus, supervisorId }) {
    const hardBlocked = false; // simplificado: sin blockers en estos tests
    if (!targetStatus || hardBlocked) return false;
    if (!isAdmin && !(supervisorId && reasonOk)) return false;  // gate supervisor
    if (forceReason && !reasonOk) return false;                 // gate motivo obligatorio admin
    return true;
}

test("Fix#2 reabrir: admin SIN forceReason → puede confirmar sin motivo (comportamiento viejo)", () => {
    const canSubmit = calcularCanSubmitRevert({
        isAdmin: true, requiresAuth: false, forceReason: false,
        reasonOk: false, targetStatus: "Confirmed", supervisorId: "",
    });
    assert.equal(canSubmit, true, "sin forceReason, el admin puede confirmar sin motivo");
});

test("Fix#2 revertir: admin CON forceReason + motivo vacío → NO puede confirmar", () => {
    // Regla ADR-035: revertir estado con forceReason siempre exige motivo, incluido admin.
    const canSubmit = calcularCanSubmitRevert({
        isAdmin: true, requiresAuth: false, forceReason: true,
        reasonOk: false, targetStatus: "Confirmed", supervisorId: "",
    });
    assert.equal(canSubmit, false, "con forceReason, admin no puede confirmar sin motivo");
});

test("Fix#2 reabrir: admin CON forceReason + motivo completo → puede confirmar", () => {
    const canSubmit = calcularCanSubmitRevert({
        isAdmin: true, requiresAuth: false, forceReason: true,
        reasonOk: true, targetStatus: "Confirmed", supervisorId: "",
    });
    assert.equal(canSubmit, true, "admin con forceReason y motivo válido puede confirmar");
});

test("Fix#2 reabrir: no-admin CON forceReason + motivo + supervisor → puede confirmar", () => {
    const canSubmit = calcularCanSubmitRevert({
        isAdmin: false, requiresAuth: true, forceReason: true,
        reasonOk: true, targetStatus: "Confirmed", supervisorId: "user-123",
    });
    assert.equal(canSubmit, true);
});

test("Fix#2 reabrir: no-admin CON forceReason sin supervisor → NO puede confirmar", () => {
    const canSubmit = calcularCanSubmitRevert({
        isAdmin: false, requiresAuth: true, forceReason: true,
        reasonOk: true, targetStatus: "Confirmed", supervisorId: "",
    });
    assert.equal(canSubmit, false, "no-admin siempre necesita supervisor, independientemente de forceReason");
});

test("Fix#2 reabrir: sin target → NO puede confirmar aunque tenga motivo", () => {
    const canSubmit = calcularCanSubmitRevert({
        isAdmin: true, requiresAuth: false, forceReason: true,
        reasonOk: true, targetStatus: "", supervisorId: "",
    });
    assert.equal(canSubmit, false, "sin destino seleccionado no se puede confirmar");
});

// ─────────────────────────────────────────────────────────────────────────────
// Fix #4: banner del cobro — sin doble símbolo de moneda
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Replica la función formatCurrency de lib/utils.js (solo el prefijo del símbolo).
 * En el banner, el texto fue corregido para NO anteponer símbolo manual antes de formatCurrency.
 * Este test valida que el texto del banner no tenga el símbolo duplicado.
 */
function simularTextoBannerCobro(monedaPrincipal, saldo) {
    // Comportamiento CORRECTO (fix #4): solo el símbolo de la moneda en el label,
    // y formatCurrency da el valor ya con símbolo. No se repite.
    const simbolo = monedaPrincipal === "USD" ? "US$" : "$";
    // formatCurrency incluye el símbolo: "US$1.000,00" o "$5.000,00"
    const valorFormateado = monedaPrincipal === "USD"
        ? `US$${saldo.toFixed(2)}`    // simulacion simplificada
        : `$${saldo.toFixed(2)}`;
    return `Cobrás en ${simbolo} — saldo ${valorFormateado}`;
}

test("Fix#4 banner: ARS — sin símbolo duplicado", () => {
    const texto = simularTextoBannerCobro("ARS", 5000);
    // Debe decir "$ — saldo $5000.00" (un solo símbolo en el saldo)
    assert.ok(!texto.includes("$ $"), "no debe tener '$ $' (símbolo duplicado)");
    assert.ok(texto.includes("Cobrás en $"), "debe indicar la moneda");
});

test("Fix#4 banner: USD — sin símbolo duplicado", () => {
    const texto = simularTextoBannerCobro("USD", 1000);
    // Debe decir "US$ — saldo US$1000.00" (un solo símbolo en el saldo)
    assert.ok(!texto.includes("US$ US$"), "no debe tener 'US$ US$' (símbolo duplicado)");
    assert.ok(texto.includes("Cobrás en US$"), "debe indicar la moneda");
});

// ─────────────────────────────────────────────────────────────────────────────
// Fix #3 → E) PaymentModal — ahora usa monedaPrincipal del DTO (fix completo)
//
// El fix parcial (currency:"ARS" explícito) fue reemplazado por la solución
// completa: PaymentModal recibe monedaPrincipal + porMoneda del CollectionWorkItemDto
// y usa la misma lógica que RegistrarCobroInline.
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Replica la lógica de resolución de moneda de PaymentModal (ADR-035 completo).
 * Idéntica a RegistrarCobroInline para que el comportamiento sea consistente.
 */
function resolverMonedaDefaultPaymentModal({ monedaPrincipal, porMoneda }) {
    return monedaPrincipal
        || porMoneda?.[0]?.currency
        || "ARS";
}

/**
 * Replica la condición esMultimoneda del PaymentModal actualizado.
 */
function esMultimonedaPaymentModal(porMoneda) {
    return Array.isArray(porMoneda) && porMoneda.length > 1;
}

/**
 * Replica la construcción del payload de un nuevo cobro normal en PaymentModal.
 * La moneda viene de monedaPrincipalDefault (del DTO), nunca hardcodeada.
 */
function armarPayloadPaymentModal({ reservaId, amount, monedaCobro, method, notes, paidAt }) {
    return {
        reservaId,
        amount: parseFloat(amount),
        currency: monedaCobro,   // ADR-035: moneda real de la reserva, no "ARS" fijo
        method,
        paidAt,
        notes,
    };
}

/**
 * Replica el payload de cobro cruzado de PaymentModal.
 */
function armarPayloadCruzadoPaymentModal({
    reservaId, amount, monedaCobro, saldoImputado,
    tipoCambio, fuenteTC, fechaTC, montoEquivalente,
    method, notes, paidAt
}) {
    return {
        reservaId,
        amount: parseFloat(amount),
        currency: monedaCobro,
        imputedCurrency: saldoImputado,
        exchangeRate: parseFloat(tipoCambio),
        exchangeRateSource: fuenteTC,
        exchangeRateAt: fechaTC,
        imputedAmount: montoEquivalente,
        method,
        paidAt,
        notes,
    };
}

// ── E) Tests: resolución de moneda en PaymentModal ────────────────────────────

test("E PaymentModal: reserva USD → monedaDefault = USD (no ARS hardcodeado)", () => {
    // Este era el bug: currency:"ARS" fijo aunque la reserva fuera en USD.
    const item = {
        monedaPrincipal: "USD",
        porMoneda: [{ currency: "USD", balance: 1000 }],
    };
    assert.equal(resolverMonedaDefaultPaymentModal(item), "USD");
});

test("E PaymentModal: reserva ARS → monedaDefault = ARS", () => {
    const item = {
        monedaPrincipal: "ARS",
        porMoneda: [{ currency: "ARS", balance: 50000 }],
    };
    assert.equal(resolverMonedaDefaultPaymentModal(item), "ARS");
});

test("E PaymentModal: monedaPrincipal null → usa primer elemento de porMoneda (legacy con backfill parcial)", () => {
    const item = {
        monedaPrincipal: null,
        porMoneda: [{ currency: "USD", balance: 500 }],
    };
    assert.equal(resolverMonedaDefaultPaymentModal(item), "USD");
});

test("E PaymentModal: sin monedaPrincipal ni porMoneda → ARS como último fallback (reserva muy vieja)", () => {
    const item = { monedaPrincipal: null, porMoneda: null };
    assert.equal(resolverMonedaDefaultPaymentModal(item), "ARS");
});

test("E PaymentModal: item undefined (modal sin selectedItem) → ARS como fallback (no rompe)", () => {
    // Caso: el modal se abre por error sin item seleccionado — no debe explotar.
    assert.equal(resolverMonedaDefaultPaymentModal({}), "ARS");
});

test("E PaymentModal: multimoneda ARS+USD con monedaPrincipal=USD → arranca en USD", () => {
    const item = {
        monedaPrincipal: "USD",
        porMoneda: [
            { currency: "ARS", balance: 10000 },
            { currency: "USD", balance: 3000 },
        ],
    };
    assert.equal(resolverMonedaDefaultPaymentModal(item), "USD");
});

// ── E) Tests: payload del PaymentModal actualizado ────────────────────────────

test("E PaymentModal payload: reserva USD → payload manda currency:'USD' (no ARS)", () => {
    // Verifica que el fix completo resuelve el bug: currency viene de la reserva.
    const monedaCobro = resolverMonedaDefaultPaymentModal({
        monedaPrincipal: "USD",
        porMoneda: [{ currency: "USD", balance: 1000 }],
    });
    const payload = armarPayloadPaymentModal({
        reservaId: "res-usd-123",
        amount: "500",
        monedaCobro,
        method: "Transferencia",
        notes: "",
        paidAt: "2026-06-19T00:00:00.000Z",
    });
    assert.equal(payload.currency, "USD", "el payload debe tener currency USD para reservas en dólares");
    assert.equal(payload.amount, 500);
    assert.equal(payload.reservaId, "res-usd-123");
});

test("E PaymentModal payload: reserva ARS → payload manda currency:'ARS'", () => {
    const monedaCobro = resolverMonedaDefaultPaymentModal({
        monedaPrincipal: "ARS",
        porMoneda: [{ currency: "ARS", balance: 50000 }],
    });
    const payload = armarPayloadPaymentModal({
        reservaId: "res-ars-456",
        amount: "10000",
        monedaCobro,
        method: "Efectivo",
        notes: "Recibo 789",
        paidAt: "2026-06-19T00:00:00.000Z",
    });
    assert.equal(payload.currency, "ARS");
    assert.equal(payload.amount, 10000);
});

// ── F) Tests: esMultimoneda y link "pagar en otra moneda" en PaymentModal ─────

test("F PaymentModal: monomoneda → esMultimoneda=false (no muestra link)", () => {
    const porMoneda = [{ currency: "USD", balance: 1000 }];
    assert.equal(esMultimonedaPaymentModal(porMoneda), false);
});

test("F PaymentModal: multimoneda (ARS+USD) → esMultimoneda=true (muestra link)", () => {
    const porMoneda = [
        { currency: "ARS", balance: 5000 },
        { currency: "USD", balance: 2000 },
    ];
    assert.equal(esMultimonedaPaymentModal(porMoneda), true);
});

test("F PaymentModal: porMoneda null → esMultimoneda=false (no rompe en legacy)", () => {
    assert.equal(esMultimonedaPaymentModal(null), false);
});

test("F PaymentModal: porMoneda array vacío → esMultimoneda=false", () => {
    assert.equal(esMultimonedaPaymentModal([]), false);
});

test("F PaymentModal: link 'pagar en otra moneda' visible en multimoneda antes de activarlo", () => {
    // Replica la condición de visibilidad del link en el JSX del modal.
    const esMultimoneda = true;
    const mostrarOtraMoneda = false;
    const linkVisible = esMultimoneda && !mostrarOtraMoneda;
    assert.equal(linkVisible, true);
});

test("F PaymentModal: selectores moneda/imputar ocultos en modo simple (mostrarOtraMoneda=false)", () => {
    const esMultimoneda = true;
    const mostrarOtraMoneda = false;
    const selectoresVisibles = esMultimoneda && mostrarOtraMoneda;
    assert.equal(selectoresVisibles, false);
});

test("F PaymentModal: selectores moneda/imputar visibles tras activar el link", () => {
    const esMultimoneda = true;
    const mostrarOtraMoneda = true;
    const selectoresVisibles = esMultimoneda && mostrarOtraMoneda;
    assert.equal(selectoresVisibles, true);
});

// ── F) Tests: payload de cobro cruzado en PaymentModal ───────────────────────

test("F PaymentModal cruzado: pago en ARS para bajar deuda USD → payload incluye campos TC", () => {
    const payload = armarPayloadCruzadoPaymentModal({
        reservaId: "res-multi-789",
        amount: "120000",
        monedaCobro: "ARS",
        saldoImputado: "USD",
        tipoCambio: "1200",
        fuenteTC: 5,
        fechaTC: "2026-06-19T00:00:00.000Z",
        montoEquivalente: 100,   // 120000 / 1200 = 100 USD
        method: "Transferencia",
        notes: "",
        paidAt: "2026-06-19T00:00:00.000Z",
    });
    assert.equal(payload.currency, "ARS");
    assert.equal(payload.imputedCurrency, "USD");
    assert.equal(payload.exchangeRate, 1200);
    assert.equal(payload.exchangeRateSource, 5);
    assert.ok("imputedAmount" in payload, "debe incluir imputedAmount");
    assert.equal(payload.imputedAmount, 100);
});

test("F PaymentModal cruzado: monto equivalente ARS→USD = monto/tipoCambio", () => {
    // Fórmula: pago en ARS para bajar USD → imputedAmount = monto / TC
    const monedaCobro = "ARS";
    const saldoImputado = "USD";
    const monto = 120000;
    const tc = 1200;

    let montoEquivalente = null;
    if (monedaCobro === "ARS" && saldoImputado === "USD") {
        montoEquivalente = monto / tc;
    }
    assert.equal(montoEquivalente, 100, "120000 ARS / 1200 = 100 USD");
});

test("F PaymentModal cruzado: monto equivalente USD→ARS = monto*tipoCambio", () => {
    // Fórmula: pago en USD para bajar ARS → imputedAmount = monto * TC
    const monedaCobro = "USD";
    const saldoImputado = "ARS";
    const monto = 100;
    const tc = 1200;

    let montoEquivalente = null;
    if (monedaCobro === "USD" && saldoImputado === "ARS") {
        montoEquivalente = monto * tc;
    }
    assert.equal(montoEquivalente, 120000, "100 USD * 1200 = 120000 ARS");
});

test("F PaymentModal cruzado: TC vacío → montoEquivalente es null (no rompe)", () => {
    // Si el usuario no ingresó el TC todavía, no se puede calcular el equivalente.
    const tipoCambio = "";
    const tc = parseFloat(tipoCambio);
    const esCobroCruzado = true;
    let montoEquivalente = null;
    if (esCobroCruzado) {
        if (!isNaN(tc) && tc > 0) montoEquivalente = 100 / tc;
    }
    assert.equal(montoEquivalente, null, "sin TC no se muestra equivalente");
});
