/**
 * Tests de lógica pura para el flujo "cierre sin multa del operador" (2026-06-28).
 *
 * Cubre:
 *   - validarMotivoCierreSinMulta: límites 5..500 del campo motivo.
 *   - puedeCerrarSinMulta: cuándo el botón "Confirmar: sin multa" está habilitado.
 *   - validarMotivoDeshacer / puedeDeshacer: mismo contrato para el panel de Admin.
 *   - detección del estado "waived" desde capabilities (lógica replicada de ReservaDetailPage).
 *   - visibilidad de la pregunta "¿El operador cobró multa?" y sus dos botones.
 *   - visibilidad del enlace "Deshacer" solo para Admin.
 *   - esErrorSaldoYaUsado (E1, 2026-07-08): detección del 409 SALDO_YA_USADO.
 *
 * Cómo correr:
 *   node --test src/features/cancellations/components/cierreSinMultaLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Réplicas de las funciones de CerrarSinMultaInline.jsx ────────────────────
// Se copian para testearlas sin DOM ni React.
// Si cambia la lógica del componente, actualizar también estas réplicas.

const MOTIVO_MIN = 5;
const MOTIVO_MAX = 500;

function validarMotivoCierreSinMulta(motivo) {
    const trimmed = (motivo ?? "").trim();
    if (trimmed.length < MOTIVO_MIN) {
        return `El motivo debe tener al menos ${MOTIVO_MIN} caracteres.`;
    }
    if (trimmed.length > MOTIVO_MAX) {
        return `El motivo no puede superar los ${MOTIVO_MAX} caracteres.`;
    }
    return null;
}

function puedeCerrarSinMulta({ motivo, submitting }) {
    if (submitting) return false;
    return validarMotivoCierreSinMulta(motivo) === null;
}

// ─── Réplica de DeshacerCierreSinMultaInline.jsx ──────────────────────────────

function validarMotivoDeshacer(motivo) {
    const trimmed = (motivo ?? "").trim();
    if (trimmed.length < MOTIVO_MIN) {
        return `El motivo debe tener al menos ${MOTIVO_MIN} caracteres.`;
    }
    if (trimmed.length > MOTIVO_MAX) {
        return `El motivo no puede superar los ${MOTIVO_MAX} caracteres.`;
    }
    return null;
}

function puedeDeshacer({ motivo, submitting }) {
    if (submitting) return false;
    return validarMotivoDeshacer(motivo) === null;
}

// ─── Réplica de la lógica de detección "waived" de ReservaDetailPage.jsx ─────
// Esta lógica determina qué variante del banner PendingOperatorRefund se muestra.

/**
 * Detecta si el paso de multa fue cerrado como "sin multa" (waived).
 *
 * Fix 2026-06-29 (reviewer): el campo anterior `canConfirmOperatorPenalty.reason` nunca
 * transportó "OperatorPenaltyWaived" en el backend real — era un campo muerto.
 * El backend ahora expone `capabilities.operatorPenaltyOutcome` con valores:
 *   "None" | "Pending" | "Confirmed" | "Waived"
 *
 * Si el campo no está (DTO viejo o capabilities ausente) → false → banner genérico (seguro).
 *
 * Réplica de la variable `yaWaived` en el bloque PendingOperatorRefund de ReservaDetailPage.
 */
function detectarWaived(reserva) {
    return reserva?.capabilities?.operatorPenaltyOutcome === "Waived";
}

/**
 * Determina si se debe mostrar la pregunta "¿El operador te cobró multa?" con
 * sus dos botones. Solo cuando la capability lo permite (allowed=true) y
 * ningún panel inline está abierto.
 *
 * Réplica de `hayMultaPendiente` + condición de paneles en ReservaDetailPage.
 */
function debeMostrarEleccion({ reserva, showMultaInline, showSinMultaInline }) {
    const hayMultaPendiente = reserva?.capabilities?.canConfirmOperatorPenalty?.allowed === true;
    if (!hayMultaPendiente) return false;
    if (showMultaInline || showSinMultaInline) return false;
    return true;
}

/**
 * Determina si el enlace "Deshacer" debe ser visible.
 * Solo Admin + estado waived + panel de deshacer cerrado.
 *
 * @param {{ reserva: object, esAdmin: boolean, showDeshacerWaiveInline: boolean }} params
 */
function debeMostrarDeshacer({ reserva, esAdmin, showDeshacerWaiveInline }) {
    if (!esAdmin) return false;
    if (showDeshacerWaiveInline) return false;
    return detectarWaived(reserva);
}

// ============================================================================
// Sección 1: validarMotivoCierreSinMulta — límite inferior (5 chars)
// ============================================================================

test("motivo vacío → error (mínimo 5 caracteres)", () => {
    const error = validarMotivoCierreSinMulta("");
    assert.notEqual(error, null);
    assert.match(error, /5/);
});

test("motivo solo espacios → error (se trimea, queda vacío)", () => {
    const error = validarMotivoCierreSinMulta("   ");
    assert.notEqual(error, null);
});

test("motivo de 4 caracteres → error (falta 1 para el mínimo)", () => {
    const error = validarMotivoCierreSinMulta("abcd");
    assert.notEqual(error, null);
});

test("motivo de exactamente 5 caracteres → válido", () => {
    const error = validarMotivoCierreSinMulta("abcde");
    assert.equal(error, null);
});

test("motivo de 5 chars con espacios al inicio/fin → válido (trim cuenta)", () => {
    // "  ok  " después de trim tiene 2 chars → inválido.
    // "  abcde  " después de trim tiene 5 chars → válido.
    const error = validarMotivoCierreSinMulta("  abcde  ");
    assert.equal(error, null);
});

test("motivo normal de 50 caracteres → válido", () => {
    const motivo = "El operador confirmó por mail que no aplica multa.";
    const error = validarMotivoCierreSinMulta(motivo);
    assert.equal(error, null);
});

// ============================================================================
// Sección 2: validarMotivoCierreSinMulta — límite superior (500 chars)
// ============================================================================

test("motivo de exactamente 500 caracteres → válido (límite exacto)", () => {
    const motivo = "x".repeat(500);
    const error = validarMotivoCierreSinMulta(motivo);
    assert.equal(error, null);
});

test("motivo de 501 caracteres → error (supera el límite)", () => {
    const motivo = "x".repeat(501);
    const error = validarMotivoCierreSinMulta(motivo);
    assert.notEqual(error, null);
    assert.match(error, /500/);
});

test("null como motivo → error (se maneja sin crash)", () => {
    const error = validarMotivoCierreSinMulta(null);
    assert.notEqual(error, null);
});

test("undefined como motivo → error (se maneja sin crash)", () => {
    const error = validarMotivoCierreSinMulta(undefined);
    assert.notEqual(error, null);
});

// ============================================================================
// Sección 3: puedeCerrarSinMulta
// ============================================================================

test("motivo válido + no submitting → puedeCerrarSinMulta=true", () => {
    assert.equal(
        puedeCerrarSinMulta({ motivo: "Operador confirmó sin penalidad", submitting: false }),
        true
    );
});

test("submitting=true → puedeCerrarSinMulta=false aunque el motivo sea válido", () => {
    // Previene doble envío mientras hay una llamada en curso.
    assert.equal(
        puedeCerrarSinMulta({ motivo: "Operador confirmó sin penalidad", submitting: true }),
        false
    );
});

test("motivo demasiado corto → puedeCerrarSinMulta=false", () => {
    assert.equal(
        puedeCerrarSinMulta({ motivo: "ok", submitting: false }),
        false
    );
});

test("motivo vacío → puedeCerrarSinMulta=false", () => {
    assert.equal(
        puedeCerrarSinMulta({ motivo: "", submitting: false }),
        false
    );
});

// ============================================================================
// Sección 4: validarMotivoDeshacer — mismo contrato que CerrarSinMulta
// ============================================================================

test("deshacer: motivo vacío → error", () => {
    assert.notEqual(validarMotivoDeshacer(""), null);
});

test("deshacer: motivo de 5 chars → válido", () => {
    assert.equal(validarMotivoDeshacer("abcde"), null);
});

test("deshacer: motivo de 501 chars → error", () => {
    assert.notEqual(validarMotivoDeshacer("x".repeat(501)), null);
});

test("deshacer: motivo de 500 chars → válido", () => {
    assert.equal(validarMotivoDeshacer("x".repeat(500)), null);
});

test("deshacer: null → error sin crash", () => {
    assert.notEqual(validarMotivoDeshacer(null), null);
});

// ============================================================================
// Sección 5: puedeDeshacer
// ============================================================================

test("deshacer: motivo válido + no submitting → true", () => {
    assert.equal(
        puedeDeshacer({ motivo: "El operador informó una penalidad tarde", submitting: false }),
        true
    );
});

test("deshacer: submitting=true → false aunque el motivo sea válido", () => {
    assert.equal(
        puedeDeshacer({ motivo: "El operador informó una penalidad tarde", submitting: true }),
        false
    );
});

test("deshacer: motivo corto → false", () => {
    assert.equal(
        puedeDeshacer({ motivo: "nope", submitting: false }),
        false
    );
});

// ============================================================================
// Sección 6: detección del estado "waived" desde capabilities.operatorPenaltyOutcome
//
// Fix 2026-06-29: el campo `canConfirmOperatorPenalty.reason` NUNCA transportó
// "OperatorPenaltyWaived" en el backend (campo muerto). El campo real es
// `capabilities.operatorPenaltyOutcome` ("None"|"Pending"|"Confirmed"|"Waived").
// ============================================================================

test("detectarWaived: operatorPenaltyOutcome='Waived' → true (estado cerrado sin multa)", () => {
    // Contrato real del backend: campo dedicado, no el campo `reason` de la capability.
    const reserva = {
        status: "PendingOperatorRefund",
        capabilities: { operatorPenaltyOutcome: "Waived" },
    };
    assert.equal(detectarWaived(reserva), true);
});

test("detectarWaived: operatorPenaltyOutcome='Pending' → false (multa todavía pendiente de resolver)", () => {
    const reserva = {
        capabilities: { operatorPenaltyOutcome: "Pending" },
    };
    assert.equal(detectarWaived(reserva), false);
});

test("detectarWaived: operatorPenaltyOutcome='Confirmed' → false (multa ya confirmada con ND)", () => {
    const reserva = {
        capabilities: { operatorPenaltyOutcome: "Confirmed" },
    };
    assert.equal(detectarWaived(reserva), false);
});

test("detectarWaived: operatorPenaltyOutcome='None' → false", () => {
    const reserva = {
        capabilities: { operatorPenaltyOutcome: "None" },
    };
    assert.equal(detectarWaived(reserva), false);
});

test("detectarWaived: capabilities ausentes → false (degradación segura, no crashea)", () => {
    const reserva = { status: "PendingOperatorRefund" };
    assert.equal(detectarWaived(reserva), false);
});

test("detectarWaived: capabilities presente pero sin operatorPenaltyOutcome → false (DTO viejo)", () => {
    // Si el backend no mandó el campo nuevo, no asumimos waived.
    const reserva = { capabilities: {} };
    assert.equal(detectarWaived(reserva), false);
});

test("detectarWaived: reserva null → false (no crashea)", () => {
    assert.equal(detectarWaived(null), false);
});

// Regresión: el campo muerto NO debe disparar la detección.
// Antes del fix, se leía `canConfirmOperatorPenalty.reason === "OperatorPenaltyWaived"`;
// ese valor NUNCA llega del backend. Confirmamos que el código nuevo no lo interpreta como waived.
test("REGRESIÓN: allowed=false + reason='OperatorPenaltyWaived' SIN operatorPenaltyOutcome → false (campo muerto ignorado)", () => {
    // Si el backend manda `reason` con el valor viejo pero no manda `operatorPenaltyOutcome`,
    // el resultado debe ser false — el código nuevo no lee ese campo obsoleto.
    const reserva = {
        status: "PendingOperatorRefund",
        capabilities: {
            canConfirmOperatorPenalty: { allowed: false, reason: "OperatorPenaltyWaived" },
            // operatorPenaltyOutcome: ausente
        },
    };
    assert.equal(detectarWaived(reserva), false, "El campo reason nunca transportó este valor — no debe disparar waived");
});

// ============================================================================
// Sección 7: visibilidad de la pregunta "¿El operador cobró multa?"
// ============================================================================

test("mostrarEleccion: allowed=true + paneles cerrados → se muestra la pregunta y los dos botones", () => {
    const reserva = {
        capabilities: { canConfirmOperatorPenalty: { allowed: true, reason: null } },
    };
    assert.equal(
        debeMostrarEleccion({ reserva, showMultaInline: false, showSinMultaInline: false }),
        true
    );
});

test("mostrarEleccion: allowed=true + showMultaInline=true → se oculta (panel naranja ya está abierto)", () => {
    // La pregunta desaparece cuando el panel de multa está visible — no duplicamos el form.
    const reserva = {
        capabilities: { canConfirmOperatorPenalty: { allowed: true, reason: null } },
    };
    assert.equal(
        debeMostrarEleccion({ reserva, showMultaInline: true, showSinMultaInline: false }),
        false
    );
});

test("mostrarEleccion: allowed=true + showSinMultaInline=true → se oculta (panel teal ya está abierto)", () => {
    const reserva = {
        capabilities: { canConfirmOperatorPenalty: { allowed: true, reason: null } },
    };
    assert.equal(
        debeMostrarEleccion({ reserva, showMultaInline: false, showSinMultaInline: true }),
        false
    );
});

test("mostrarEleccion: allowed=false → no se muestra (paso no accionable)", () => {
    // Si allowed=false por cualquier motivo (NC sin CAE, ND ya en juego, waived),
    // no se muestran los dos botones — el agente no puede actuar ahora.
    const reserva = {
        capabilities: { canConfirmOperatorPenalty: { allowed: false, reason: "CreditNoteNotYetIssued" } },
    };
    assert.equal(
        debeMostrarEleccion({ reserva, showMultaInline: false, showSinMultaInline: false }),
        false
    );
});

test("mostrarEleccion: capabilities ausentes → no se muestra (seguro)", () => {
    const reserva = { status: "PendingOperatorRefund" };
    assert.equal(
        debeMostrarEleccion({ reserva, showMultaInline: false, showSinMultaInline: false }),
        false
    );
});

// ============================================================================
// Sección 8: visibilidad del enlace "Deshacer" (solo Admin)
// Usa operatorPenaltyOutcome — campo real del backend (fix 2026-06-29).
// ============================================================================

test("mostrarDeshacer: Admin + operatorPenaltyOutcome='Waived' + panel cerrado → link visible", () => {
    const reserva = {
        capabilities: { operatorPenaltyOutcome: "Waived" },
    };
    assert.equal(
        debeMostrarDeshacer({ reserva, esAdmin: true, showDeshacerWaiveInline: false }),
        true
    );
});

test("mostrarDeshacer: no-Admin + operatorPenaltyOutcome='Waived' → link NO visible (vendedor no ve el deshacer)", () => {
    // CRÍTICO: el link de deshacer no debe mostrarse a usuarios sin rol Admin.
    const reserva = {
        capabilities: { operatorPenaltyOutcome: "Waived" },
    };
    assert.equal(
        debeMostrarDeshacer({ reserva, esAdmin: false, showDeshacerWaiveInline: false }),
        false
    );
});

test("mostrarDeshacer: Admin + operatorPenaltyOutcome='Pending' → link NO visible (multa todavía pendiente)", () => {
    const reserva = {
        capabilities: { operatorPenaltyOutcome: "Pending" },
    };
    assert.equal(
        debeMostrarDeshacer({ reserva, esAdmin: true, showDeshacerWaiveInline: false }),
        false
    );
});

test("mostrarDeshacer: Admin + waived + panel abierto → link oculto (ya está el form visible)", () => {
    // El link desaparece cuando el panel de deshacer está abierto — no duplicamos el form.
    const reserva = {
        capabilities: { operatorPenaltyOutcome: "Waived" },
    };
    assert.equal(
        debeMostrarDeshacer({ reserva, esAdmin: true, showDeshacerWaiveInline: true }),
        false
    );
});

test("mostrarDeshacer: Admin + capabilities ausentes → false (no mostramos el link sin datos)", () => {
    const reserva = { status: "PendingOperatorRefund" };
    assert.equal(
        debeMostrarDeshacer({ reserva, esAdmin: true, showDeshacerWaiveInline: false }),
        false
    );
});

test("mostrarDeshacer: Admin + operatorPenaltyOutcome='Confirmed' → false (multa ya confirmada con ND, no waived)", () => {
    const reserva = {
        capabilities: { operatorPenaltyOutcome: "Confirmed" },
    };
    assert.equal(
        debeMostrarDeshacer({ reserva, esAdmin: true, showDeshacerWaiveInline: false }),
        false
    );
});

// ============================================================================
// Sección 9: contrato del payload que va al backend (PATCH waive-penalty y revert-waive)
// ============================================================================

/**
 * Construye el payload de waive-penalty.
 * El backend solo pide { reason: string }, nada más.
 *
 * Réplica de la llamada en CerrarSinMultaInline.handleConfirmar.
 */
function construirPayloadWaive(motivo) {
    return { reason: motivo.trim() };
}

/**
 * Construye el payload de revert-waive.
 * El backend solo pide { reason: string }, nada más.
 *
 * Réplica de la llamada en DeshacerCierreSinMultaInline.handleDeshacer.
 */
function construirPayloadRevertWaive(motivo) {
    return { reason: motivo.trim() };
}

test("payload waive-penalty: tiene la clave 'reason' con el motivo trimmed", () => {
    const payload = construirPayloadWaive("  El operador confirmó sin penalidad  ");
    assert.equal(payload.reason, "El operador confirmó sin penalidad");
});

test("payload waive-penalty: no envía monto, moneda, fecha ni comprobante (cierre sin ND)", () => {
    // CRÍTICO: waive-penalty NO es una emisión de ND — no hay campos de plata.
    const payload = construirPayloadWaive("Motivo de cierre");
    assert.equal(Object.keys(payload).length, 1);
    assert.equal(Object.keys(payload)[0], "reason");
    assert.equal("monto" in payload, false);
    assert.equal("amount" in payload, false);
    assert.equal("currency" in payload, false);
});

test("payload revert-waive: tiene la clave 'reason' con el motivo trimmed", () => {
    const payload = construirPayloadRevertWaive("  El operador informó penalidad tarde  ");
    assert.equal(payload.reason, "El operador informó penalidad tarde");
});

test("payload revert-waive: solo la clave 'reason' (Admin solo explica por qué deshace)", () => {
    const payload = construirPayloadRevertWaive("Motivo del deshacer");
    assert.equal(Object.keys(payload).length, 1);
    assert.equal(Object.keys(payload)[0], "reason");
});

// ============================================================================
// Sección 6: esErrorSaldoYaUsado (E1, spec "el paso de multa vive en la ficha", 2026-07-08)
// Réplica de la función homónima en DeshacerCierreSinMultaInline.jsx.
// ============================================================================

function esErrorSaldoYaUsado(error) {
    return error?.status === 409 && error?.payload?.code === "SALDO_YA_USADO";
}

test("esErrorSaldoYaUsado: 409 + code SALDO_YA_USADO → true", () => {
    const error = { status: 409, payload: { code: "SALDO_YA_USADO", message: "El cliente ya usó ese saldo a favor." } };
    assert.equal(esErrorSaldoYaUsado(error), true);
});

test("esErrorSaldoYaUsado: 409 con otro code → false", () => {
    const error = { status: 409, payload: { code: "CONCURRENT_EDIT" } };
    assert.equal(esErrorSaldoYaUsado(error), false);
});

test("esErrorSaldoYaUsado: 409 sin payload → false", () => {
    assert.equal(esErrorSaldoYaUsado({ status: 409 }), false);
});

test("esErrorSaldoYaUsado: otro status con code SALDO_YA_USADO → false (el código solo aplica al 409)", () => {
    const error = { status: 400, payload: { code: "SALDO_YA_USADO" } };
    assert.equal(esErrorSaldoYaUsado(error), false);
});

test("esErrorSaldoYaUsado: error sin status ni payload → false", () => {
    assert.equal(esErrorSaldoYaUsado({}), false);
});
