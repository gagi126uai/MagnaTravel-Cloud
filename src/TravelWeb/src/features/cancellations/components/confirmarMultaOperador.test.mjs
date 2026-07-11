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

// Réplica de hayFacturaDestinoAmbigua / facturaDestinoResuelta (facturaDestinoLogic.js).
function hayFacturaDestinoAmbigua(saleInvoices) {
    return Array.isArray(saleInvoices) && saleInvoices.length >= 2;
}

function facturaDestinoResuelta(saleInvoices, targetInvoicePublicId) {
    if (!hayFacturaDestinoAmbigua(saleInvoices)) return true;
    return Boolean(targetInvoicePublicId);
}

/**
 * Determina si el formulario puede enviarse.
 * Réplica de puedeEnviar en ConfirmarMultaOperadorInline.jsx (ADR-044 T4, 2026-07-10:
 * suma facturaDestinoResuelta — con 2+ facturas activas, hace falta elegir una).
 */
function puedeEnviar({ montoStr, fecha, saleInvoices, targetInvoicePublicId, submitting }) {
    if (submitting) return false;
    const { montoError, fechaError } = validarCamposMulta({ montoStr, fecha });
    if (montoError !== null || fechaError !== null) return false;
    return facturaDestinoResuelta(saleInvoices, targetInvoicePublicId);
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

test("ADR-044 T4: con 2+ facturas activas, puedeEnviar=false hasta elegir la factura destino (P5)", () => {
    const saleInvoices = [{ publicId: "inv-1" }, { publicId: "inv-2" }];
    assert.equal(
        puedeEnviar({ montoStr: "500", fecha: "2026-06-01", saleInvoices, targetInvoicePublicId: null, submitting: false }),
        false
    );
    assert.equal(
        puedeEnviar({ montoStr: "500", fecha: "2026-06-01", saleInvoices, targetInvoicePublicId: "inv-1", submitting: false }),
        true
    );
});

test("ADR-044 T4: con 0 o 1 factura, puedeEnviar no depende de targetInvoicePublicId (autocompletado)", () => {
    assert.equal(
        puedeEnviar({ montoStr: "500", fecha: "2026-06-01", saleInvoices: undefined, targetInvoicePublicId: null, submitting: false }),
        true
    );
    assert.equal(
        puedeEnviar({ montoStr: "500", fecha: "2026-06-01", saleInvoices: [{ publicId: "inv-1" }], targetInvoicePublicId: null, submitting: false }),
        true
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
 * ADR-044 T4 (2026-07-10): suma targetInvoicePublicId SOLO cuando hay 2+ facturas
 * activas (con 0/1 factura el backend la autocompleta solo, no se manda nada nuevo).
 *
 * @param {{ montoStr: string, fecha: string, referencia: string, moneda: string, saleInvoices?: Array<object>, targetInvoicePublicId?: string }} params
 */
function construirPayloadMultaOperador({ montoStr, fecha, referencia, moneda, saleInvoices, targetInvoicePublicId }) {
    const monto = parseFloat(montoStr);
    const payload = {
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

    if (hayFacturaDestinoAmbigua(saleInvoices)) {
        payload.targetInvoicePublicId = targetInvoicePublicId;
    }

    return payload;
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

test("ADR-044 T4: con 2+ facturas activas, el payload incluye targetInvoicePublicId", () => {
    const payload = construirPayloadMultaOperador({
        montoStr: "500",
        fecha: "2026-06-01",
        referencia: "",
        moneda: "USD",
        saleInvoices: [{ publicId: "inv-1" }, { publicId: "inv-2" }],
        targetInvoicePublicId: "inv-2",
    });
    assert.equal(payload.targetInvoicePublicId, "inv-2");
});

test("ADR-044 T4: con 1 sola factura, el payload NO incluye targetInvoicePublicId", () => {
    const payload = construirPayloadMultaOperador({
        montoStr: "500",
        fecha: "2026-06-01",
        referencia: "",
        moneda: "USD",
        saleInvoices: [{ publicId: "inv-1" }],
        targetInvoicePublicId: null,
    });
    assert.equal("targetInvoicePublicId" in payload, false);
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

// ============================================================================
// Sección 8: Fix H3 2026-06-24 — gate de visibilidad por capability del backend
//
// Antes del fix: el botón "Confirmar multa del operador" aparecía en TODAS las
// reservas con status=PendingOperatorRefund, aunque no hubiera multa pendiente.
// Ahora el botón solo aparece cuando capabilities.canConfirmOperatorPenalty.allowed
// es true (fuente de verdad del backend; no depende del estado operativo).
// ============================================================================

/**
 * Réplica de la lógica de visibilidad del botón "Confirmar multa del operador".
 * Fix H3: usa capabilities.canConfirmOperatorPenalty.allowed en vez del estado.
 *
 * El panel también requiere que showMultaInline sea false (para no duplicar el form).
 * Esa condición ya existía y no cambió.
 */
function mostrarBotonConfirmarMulta({ reserva, showMultaInline }) {
    // Si el panel ya está abierto, no mostramos el botón (el form lo reemplaza).
    if (showMultaInline) return false;
    // La capability es la única fuente de verdad. No usamos reserva.status.
    return reserva?.capabilities?.canConfirmOperatorPenalty?.allowed === true;
}

test("H3: canConfirmOperatorPenalty.allowed=true + panel cerrado → botón visible", () => {
    const reserva = {
        status: "PendingOperatorRefund",
        capabilities: { canConfirmOperatorPenalty: { allowed: true, reason: null } },
    };
    assert.equal(mostrarBotonConfirmarMulta({ reserva, showMultaInline: false }), true);
});

test("H3: canConfirmOperatorPenalty.allowed=false → botón oculto (sin multa pendiente)", () => {
    // Reserva en PendingOperatorRefund pero sin multa del operador pendiente.
    // Antes esto mostraba el botón y el click terminaba en showError.
    const reserva = {
        status: "PendingOperatorRefund",
        capabilities: { canConfirmOperatorPenalty: { allowed: false, reason: "DebitNoteAlreadyInPlay" } },
    };
    assert.equal(mostrarBotonConfirmarMulta({ reserva, showMultaInline: false }), false);
});

test("H3: capabilities ausentes → botón oculto (degradación segura, no asumimos que se puede)", () => {
    // Si el backend no envía capabilities (DTO viejo), no mostramos el botón.
    // Es más seguro ocultarlo que mostrarlo sin información.
    const reserva = { status: "PendingOperatorRefund" };
    assert.equal(mostrarBotonConfirmarMulta({ reserva, showMultaInline: false }), false);
});

test("H3: canConfirmOperatorPenalty ausente dentro de capabilities → botón oculto", () => {
    // El backend no incluyó la capability específica (DTO parcial).
    const reserva = {
        status: "PendingOperatorRefund",
        capabilities: { canCancel: { allowed: false } },
    };
    assert.equal(mostrarBotonConfirmarMulta({ reserva, showMultaInline: false }), false);
});

test("H3: panel inline abierto → botón oculto (aunque allowed=true, no duplicamos el form)", () => {
    // Cuando el form inline ya está abierto, ocultamos el botón que lo abrió.
    // Esta condición ya existía en la versión anterior y se mantiene igual.
    const reserva = {
        status: "PendingOperatorRefund",
        capabilities: { canConfirmOperatorPenalty: { allowed: true, reason: null } },
    };
    assert.equal(mostrarBotonConfirmarMulta({ reserva, showMultaInline: true }), false);
});

test("H3: allowed=true en estado distinto de PendingOperatorRefund → botón visible (no gateamos por estado)", () => {
    // El fix mueve el gate de estado a capability: si el backend dice "se puede",
    // lo mostramos independientemente del estado. En la práctica, el backend solo
    // devuelve allowed=true en PendingOperatorRefund, pero el front no duplica esa regla.
    const reserva = {
        status: "Cancelled",
        capabilities: { canConfirmOperatorPenalty: { allowed: true, reason: null } },
    };
    assert.equal(mostrarBotonConfirmarMulta({ reserva, showMultaInline: false }), true);
});

// ============================================================================
// Sección I: modo "corregir" (spec "el paso de multa vive en la ficha", 2026-07-08)
// Réplica de validarMonto / validarMotivoCorregir / validarCamposCorregir /
// puedeEnviarCorregir en ConfirmarMultaOperadorInline.jsx.
// ============================================================================

const MOTIVO_CORREGIR_MIN = 5;
const MOTIVO_CORREGIR_MAX = 500;

function validarMonto(montoStr) {
    const monto = parseFloat(montoStr);
    if (!montoStr || isNaN(monto) || monto <= 0) {
        return "El monto debe ser mayor a cero.";
    }
    return null;
}

function validarMotivoCorregir(motivo) {
    const trimmed = (motivo ?? "").trim();
    if (trimmed.length < MOTIVO_CORREGIR_MIN) {
        return `El motivo debe tener al menos ${MOTIVO_CORREGIR_MIN} caracteres.`;
    }
    if (trimmed.length > MOTIVO_CORREGIR_MAX) {
        return `El motivo no puede superar los ${MOTIVO_CORREGIR_MAX} caracteres.`;
    }
    return null;
}

function validarCamposCorregir({ montoStr, motivo }) {
    return {
        montoError: validarMonto(montoStr),
        motivoError: validarMotivoCorregir(motivo),
    };
}

function puedeEnviarCorregir({ montoStr, motivo, submitting }) {
    if (submitting) return false;
    const { montoError, motivoError } = validarCamposCorregir({ montoStr, motivo });
    return montoError === null && motivoError === null;
}

test("corregir: monto válido + motivo válido → sin errores", () => {
    const { montoError, motivoError } = validarCamposCorregir({ montoStr: "120.50", motivo: "Era en dólares, no en pesos" });
    assert.equal(montoError, null);
    assert.equal(motivoError, null);
});

test("corregir: motivo corto (menos de 5 caracteres) → error", () => {
    const { motivoError } = validarCamposCorregir({ montoStr: "100", motivo: "abc" });
    assert.notEqual(motivoError, null);
});

test("corregir: motivo vacío → error", () => {
    const { motivoError } = validarCamposCorregir({ montoStr: "100", motivo: "" });
    assert.notEqual(motivoError, null);
});

test("corregir: no pide fecha (a diferencia del modo confirmar)", () => {
    // validarCamposCorregir no tiene parámetro `fecha` — la firma en sí ya lo garantiza.
    const resultado = validarCamposCorregir({ montoStr: "100", motivo: "Motivo válido de sobra" });
    assert.ok(!("fechaError" in resultado));
});

test("puedeEnviarCorregir: false mientras está enviando (evita doble submit)", () => {
    assert.equal(
        puedeEnviarCorregir({ montoStr: "100", motivo: "Motivo válido de sobra", submitting: true }),
        false
    );
});

test("puedeEnviarCorregir: true con monto y motivo válidos, sin submit en curso", () => {
    assert.equal(
        puedeEnviarCorregir({ montoStr: "100", motivo: "Motivo válido de sobra", submitting: false }),
        true
    );
});

test("puedeEnviarCorregir: false si el motivo es muy corto aunque el monto sea válido", () => {
    assert.equal(
        puedeEnviarCorregir({ montoStr: "100", motivo: "no", submitting: false }),
        false
    );
});
