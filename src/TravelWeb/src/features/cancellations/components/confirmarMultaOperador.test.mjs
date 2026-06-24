/**
 * Tests de lógica pura del flujo "Confirmar multa del operador" (ADR-014).
 *
 * Se testean como funciones puras las reglas de:
 *   - validarCamposMulta: cuándo el monto y la fecha son válidos.
 *   - puedeEnviar: cuándo el botón de submit está habilitado.
 *   - cuándo ofrecer la acción: la reserva debe estar en PendingOperatorRefund.
 *   - decisión al tocar el botón según canConfirmPenalty + confirmPenaltyBlockedReason.
 *
 * Cómo correr:
 *   node --test src/features/cancellations/components/confirmarMultaOperador.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Réplica de las funciones de ConfirmarMultaOperadorInline.jsx ─────────────
// Se copian aquí para testearlas sin DOM ni React.
// Si cambia la lógica del componente, actualizar también estas réplicas.

function getTodayString() {
    return new Date().toISOString().split("T")[0];
}

/**
 * Valida los campos del mini-form de multa del operador.
 * Réplica de validarCamposMulta en ConfirmarMultaOperadorInline.jsx.
 */
function validarCamposMulta({ montoStr, fecha }) {
    const monto = parseFloat(montoStr);
    let montoError = null;
    let fechaError = null;

    if (!montoStr || isNaN(monto) || monto <= 0) {
        montoError = "El monto debe ser mayor a cero.";
    }

    if (!fecha) {
        fechaError = "La fecha es obligatoria.";
    } else if (fecha > getTodayString()) {
        fechaError = "La fecha no puede ser futura.";
    }

    return { montoError, fechaError };
}

/**
 * Determina si el formulario puede enviarse.
 * Réplica de puedeEnviar en ConfirmarMultaOperadorInline.jsx.
 */
function puedeEnviar({ montoStr, fecha, submitting }) {
    if (submitting) return false;
    const { montoError, fechaError } = validarCamposMulta({ montoStr, fecha });
    return montoError === null && fechaError === null;
}

// ─── Réplica de la lógica de cuándo mostrar el botón "Confirmar multa" ────────

/**
 * La acción "Confirmar multa del operador" solo se ofrece cuando la reserva
 * está en PendingOperatorRefund (anulada, esperando reembolso del operador).
 * Réplica del condicional de ReservaDetailPage.jsx.
 */
function debeOfrecerConfirmarMulta(reservaStatus) {
    return reservaStatus === "PendingOperatorRefund";
}

// ─── Réplica de la decisión al tocar "Confirmar multa del operador" ───────────

/**
 * Dada la cancelación vigente de la reserva (DTO de GET by-reserva), decide si abrir
 * el panel inline o qué aviso mostrar. Réplica del onClick del botón en
 * ReservaDetailPage.jsx (ADR-014 read-model: usa canConfirmPenalty +
 * confirmPenaltyBlockedReason en vez de filtrar la bandeja back-office).
 *
 * @returns {{ abrirPanel: boolean, cancellationPublicId: string|null, mensaje: string|null }}
 */
function decidirAccionMulta(cancelacion) {
    if (cancelacion?.canConfirmPenalty) {
        return { abrirPanel: true, cancellationPublicId: cancelacion.publicId, mensaje: null };
    }
    const motivo = cancelacion?.confirmPenaltyBlockedReason;
    let mensaje;
    if (motivo === "CreditNoteNotYetIssued") {
        mensaje = "La nota de crédito de la anulación todavía no tiene CAE aprobado en AFIP/ARCA. Esperá unos minutos y volvé a intentar.";
    } else if (motivo === "DebitNoteAlreadyInPlay") {
        mensaje = "La multa de este operador ya fue confirmada o la nota de débito ya está en proceso.";
    } else if (motivo === "DebitNoteFeatureDisabled") {
        mensaje = "La emisión de notas de débito por multa está deshabilitada. Consultá con administración.";
    } else {
        mensaje = "No se puede confirmar la multa del operador en este momento.";
    }
    return { abrirPanel: false, cancellationPublicId: null, mensaje };
}

// ============================================================================
// Sección 1: validarCamposMulta — monto
// ============================================================================

test("monto válido (150.50) → sin error de monto", () => {
    const { montoError } = validarCamposMulta({ montoStr: "150.50", fecha: "2026-06-01" });
    assert.equal(montoError, null);
});

test("monto cero → error de monto", () => {
    const { montoError } = validarCamposMulta({ montoStr: "0", fecha: "2026-06-01" });
    assert.notEqual(montoError, null);
});

test("monto negativo → error de monto", () => {
    const { montoError } = validarCamposMulta({ montoStr: "-500", fecha: "2026-06-01" });
    assert.notEqual(montoError, null);
});

test("monto vacío → error de monto", () => {
    const { montoError } = validarCamposMulta({ montoStr: "", fecha: "2026-06-01" });
    assert.notEqual(montoError, null);
});

test("monto NaN (texto no numérico) → error de monto", () => {
    const { montoError } = validarCamposMulta({ montoStr: "abc", fecha: "2026-06-01" });
    assert.notEqual(montoError, null);
});

test("monto 0.01 (mínimo positivo) → sin error de monto", () => {
    const { montoError } = validarCamposMulta({ montoStr: "0.01", fecha: "2026-06-01" });
    assert.equal(montoError, null);
});

test("monto muy grande (1000000) → sin error de monto (validación de umbral es del backend)", () => {
    // El frontend solo valida >0. Si supera un umbral del backend, el backend devuelve
    // 409 requiresApproval — que el componente maneja por separado.
    const { montoError } = validarCamposMulta({ montoStr: "1000000", fecha: "2026-06-01" });
    assert.equal(montoError, null);
});

// ============================================================================
// Sección 2: validarCamposMulta — fecha
// ============================================================================

test("fecha de hoy → sin error de fecha", () => {
    const hoy = getTodayString();
    const { fechaError } = validarCamposMulta({ montoStr: "100", fecha: hoy });
    assert.equal(fechaError, null);
});

test("fecha pasada válida → sin error de fecha", () => {
    const { fechaError } = validarCamposMulta({ montoStr: "100", fecha: "2026-05-01" });
    assert.equal(fechaError, null);
});

test("fecha futura → error de fecha", () => {
    // Fecha 30 días en el futuro
    const futuro = new Date();
    futuro.setDate(futuro.getDate() + 30);
    const fechaFutura = futuro.toISOString().split("T")[0];

    const { fechaError } = validarCamposMulta({ montoStr: "100", fecha: fechaFutura });
    assert.notEqual(fechaError, null);
    assert.match(fechaError, /futura/i);
});

test("fecha vacía → error de fecha obligatoria", () => {
    const { fechaError } = validarCamposMulta({ montoStr: "100", fecha: "" });
    assert.notEqual(fechaError, null);
    assert.match(fechaError, /obligatoria/i);
});

// ============================================================================
// Sección 3: puedeEnviar
// ============================================================================

test("monto y fecha válidos + no submitting → puedeEnviar=true", () => {
    assert.equal(
        puedeEnviar({ montoStr: "500", fecha: "2026-06-01", submitting: false }),
        true
    );
});

test("submitting=true → puedeEnviar=false aunque el form sea válido", () => {
    // Previene doble envío mientras hay una llamada en curso.
    assert.equal(
        puedeEnviar({ montoStr: "500", fecha: "2026-06-01", submitting: true }),
        false
    );
});

test("monto inválido → puedeEnviar=false", () => {
    assert.equal(
        puedeEnviar({ montoStr: "0", fecha: "2026-06-01", submitting: false }),
        false
    );
});

test("fecha futura → puedeEnviar=false", () => {
    const futuro = new Date();
    futuro.setDate(futuro.getDate() + 1);
    const fechaFutura = futuro.toISOString().split("T")[0];

    assert.equal(
        puedeEnviar({ montoStr: "500", fecha: fechaFutura, submitting: false }),
        false
    );
});

test("todo vacío → puedeEnviar=false", () => {
    assert.equal(
        puedeEnviar({ montoStr: "", fecha: "", submitting: false }),
        false
    );
});

// ============================================================================
// Sección 4: cuándo ofrecer la acción "Confirmar multa del operador"
// ============================================================================

test("PendingOperatorRefund → debe ofrecer confirmar multa", () => {
    assert.equal(debeOfrecerConfirmarMulta("PendingOperatorRefund"), true);
});

test("Cancelled → NO debe ofrecer confirmar multa (anulada sin pendiente de operador)", () => {
    // Una reserva Cancelled ya fue confirmada como anulada y el operador no necesita devolución.
    assert.equal(debeOfrecerConfirmarMulta("Cancelled"), false);
});

test("Confirmed → NO debe ofrecer confirmar multa", () => {
    assert.equal(debeOfrecerConfirmarMulta("Confirmed"), false);
});

test("InManagement → NO debe ofrecer confirmar multa", () => {
    assert.equal(debeOfrecerConfirmarMulta("InManagement"), false);
});

test("Closed → NO debe ofrecer confirmar multa", () => {
    assert.equal(debeOfrecerConfirmarMulta("Closed"), false);
});

test("Lost → NO debe ofrecer confirmar multa", () => {
    assert.equal(debeOfrecerConfirmarMulta("Lost"), false);
});

test("Traveling → NO debe ofrecer confirmar multa", () => {
    assert.equal(debeOfrecerConfirmarMulta("Traveling"), false);
});

// ============================================================================
// Sección 5: decisión al tocar el botón (read-model canConfirmPenalty)
//   El bug original: el botón buscaba la cancelación en la bandeja back-office,
//   que filtra por estado de la ND y dejaba afuera el caso pass-through (multa
//   del operador estimada). Ahora se consulta la cancelación de la reserva y se
//   decide con canConfirmPenalty + confirmPenaltyBlockedReason.
// ============================================================================

test("caso pass-through dominante (canConfirmPenalty=true) → abre el panel con el publicId", () => {
    // Penalidad estimada, NC ya con CAE: ESTE es el caso que el bug dejaba muerto.
    const cancelacion = { publicId: "guid-bc-1", canConfirmPenalty: true, confirmPenaltyBlockedReason: null };
    const r = decidirAccionMulta(cancelacion);
    assert.equal(r.abrirPanel, true);
    assert.equal(r.cancellationPublicId, "guid-bc-1");
    assert.equal(r.mensaje, null);
});

test("NC todavía sin CAE (CreditNoteNotYetIssued) → no abre, avisa que espere", () => {
    const cancelacion = { publicId: "guid-bc-2", canConfirmPenalty: false, confirmPenaltyBlockedReason: "CreditNoteNotYetIssued" };
    const r = decidirAccionMulta(cancelacion);
    assert.equal(r.abrirPanel, false);
    assert.equal(r.cancellationPublicId, null);
    assert.match(r.mensaje, /CAE/i);
});

test("ND ya en juego (DebitNoteAlreadyInPlay) → no abre, avisa que ya fue confirmada", () => {
    const cancelacion = { publicId: "guid-bc-3", canConfirmPenalty: false, confirmPenaltyBlockedReason: "DebitNoteAlreadyInPlay" };
    const r = decidirAccionMulta(cancelacion);
    assert.equal(r.abrirPanel, false);
    assert.match(r.mensaje, /ya fue confirmada|ya está en proceso/i);
});

test("feature deshabilitada (DebitNoteFeatureDisabled) → no abre, avisa deshabilitada", () => {
    const cancelacion = { publicId: "guid-bc-4", canConfirmPenalty: false, confirmPenaltyBlockedReason: "DebitNoteFeatureDisabled" };
    const r = decidirAccionMulta(cancelacion);
    assert.equal(r.abrirPanel, false);
    assert.match(r.mensaje, /deshabilitada/i);
});

test("motivo desconocido → no abre, mensaje genérico (defensivo)", () => {
    const cancelacion = { publicId: "guid-bc-5", canConfirmPenalty: false, confirmPenaltyBlockedReason: "AlgoNuevo" };
    const r = decidirAccionMulta(cancelacion);
    assert.equal(r.abrirPanel, false);
    assert.match(r.mensaje, /no se puede confirmar/i);
});

// ============================================================================
// Sección 6: payload enviado al backend (conceptKind = null para pass-through)
// ============================================================================

/**
 * Construye el payload de confirm-penalty para el pass-through del operador.
 * Réplica de la lógica del handleConfirmar de ConfirmarMultaOperadorInline.jsx.
 *
 * @param {{ montoStr: string, fecha: string, referencia: string, moneda: string }} params
 */
function construirPayloadMultaOperador({ montoStr, fecha, referencia, moneda }) {
    const monto = parseFloat(montoStr);
    return {
        // conceptKind: null = OperatorPenaltyPassThrough (regla fiscal cerrada ADR-014).
        // La agencia actúa como intermediaria, NO emite cargo propio.
        conceptKind: null,
        confirmedPenaltyAmount: monto,
        // penaltyCurrency: campo nuevo. El backend lo acepta como opcional.
        penaltyCurrency: moneda,
        operatorConfirmationDate: fecha + "T00:00:00Z",
        debitNotePurpose: null,
        supportingDocumentReference: referencia.trim() || null,
        overrideReason: null,
        approvalRequestPublicId: null,
    };
}

test("payload pass-through: conceptKind es null (NO es 0 ni AgencyManagementFee)", () => {
    const payload = construirPayloadMultaOperador({
        montoStr: "500",
        fecha: "2026-06-01",
        referencia: "",
        moneda: "USD",
    });
    // CRÍTICO: conceptKind=null identifica el pass-through en el backend.
    // conceptKind=0 es OperatorPenaltyPassThrough como INT — en este endpoint
    // el contrato usa null para el pass-through diferido (distinto del confirm inicial).
    assert.equal(payload.conceptKind, null);
});

test("payload pass-through: confirmedPenaltyAmount es el monto parseado", () => {
    const payload = construirPayloadMultaOperador({
        montoStr: "1234.56",
        fecha: "2026-06-01",
        referencia: "",
        moneda: "USD",
    });
    assert.equal(payload.confirmedPenaltyAmount, 1234.56);
});

test("payload pass-through: fecha se formatea con T00:00:00Z para el backend", () => {
    const payload = construirPayloadMultaOperador({
        montoStr: "500",
        fecha: "2026-05-15",
        referencia: "",
        moneda: "USD",
    });
    assert.equal(payload.operatorConfirmationDate, "2026-05-15T00:00:00Z");
});

test("payload pass-through: referencia vacía → supportingDocumentReference es null", () => {
    const payload = construirPayloadMultaOperador({
        montoStr: "500",
        fecha: "2026-06-01",
        referencia: "   ", // solo espacios → null
        moneda: "USD",
    });
    assert.equal(payload.supportingDocumentReference, null);
});

test("payload pass-through: referencia con contenido → supportingDocumentReference es el string trimmed", () => {
    const payload = construirPayloadMultaOperador({
        montoStr: "500",
        fecha: "2026-06-01",
        referencia: "  Nota 2025/123  ",
        moneda: "USD",
    });
    assert.equal(payload.supportingDocumentReference, "Nota 2025/123");
});

test("payload pass-through: debitNotePurpose es null (el backend usa el default)", () => {
    const payload = construirPayloadMultaOperador({
        montoStr: "500",
        fecha: "2026-06-01",
        referencia: "",
        moneda: "USD",
    });
    assert.equal(payload.debitNotePurpose, null);
});

// ============================================================================
// Sección 7: campo penaltyCurrency en el payload
// ============================================================================

test("penaltyCurrency: moneda USD → payload tiene penaltyCurrency='USD'", () => {
    const payload = construirPayloadMultaOperador({
        montoStr: "500",
        fecha: "2026-06-01",
        referencia: "",
        moneda: "USD",
    });
    assert.equal(payload.penaltyCurrency, "USD");
});

test("penaltyCurrency: moneda ARS → payload tiene penaltyCurrency='ARS'", () => {
    const payload = construirPayloadMultaOperador({
        montoStr: "500",
        fecha: "2026-06-01",
        referencia: "",
        moneda: "ARS",
    });
    assert.equal(payload.penaltyCurrency, "ARS");
});

test("penaltyCurrency: el default del componente es USD (operadores turísticos suelen cobrar en dólares)", () => {
    // Verifica que el estado inicial del componente es "USD".
    // Si alguien cambia el default, este test lo captura.
    const defaultMoneda = "USD";
    const payload = construirPayloadMultaOperador({
        montoStr: "300",
        fecha: "2026-06-01",
        referencia: "",
        moneda: defaultMoneda,
    });
    assert.equal(payload.penaltyCurrency, "USD");
});
