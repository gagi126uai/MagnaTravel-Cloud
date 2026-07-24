/**
 * Tests de lógica pura de CancelarReservaInline.jsx (guia-ux-gaston.md 2026-06-25).
 *
 * Testea:
 *   1. determinarCasoAnulacion: discriminador del caso basado en cancellationCase
 *      y fallback legacy a requiresInvoiceAnnulmentToCancel.
 *   2. formatearMontosSaldoAFavor: formateo de montos para el cartel celeste
 *      (caso PaymentsToCredit, separados por " · ", nunca sumados entre monedas).
 *   3. Lógica de routing de API: qué camino técnico corresponde a cada caso.
 *   4. Mensajes de éxito: texto exacto por caso (guia UX 2026-06-25).
 *   5. Visibilidad de carteles: qué cartel se muestra por caso.
 *   6. Coherencia entre discriminador, cartel y mensaje.
 *   7. Textos anclados: las constantes del módulo de copy contienen el texto exacto de la guía.
 *   8. Mapeo de errores para el caso PaymentsToCredit (400/403/404/409).
 *   9. Mapeo de errores del flujo multi-factura (confirmar y reintentar): 409 requiresApproval
 *      vs 409 genérico vs otros errores.
 *   10. Alternancia cartel del caso vs cartel de error (P4-2, spec
 *       docs/ux/2026-07-22-p4-retoques-circuito-proveedor.md, P2=A): en el estado "form" nunca
 *       conviven los dos carteles — con error, solo se ve el error.
 *
 * ADR-042 (2026-07-01): la lógica del flujo "anular con VARIAS facturas" (aviso previo,
 * "¿Seguro?", avance por nota, éxito/revisión, franja "en revisión") vive en un módulo
 * aparte — multiCreditNoteFlow.js — con sus propios tests en multiCreditNoteFlow.test.mjs.
 * Acá solo se actualizó el mapeo de errores: el mensaje especial de INV-100 desapareció.
 *
 * Fix bug prod (2026-07-02): 409 requiresApproval en el confirm/retry multi-factura caía al
 * mensaje genérico "probá de nuevo" — no explicaba qué pasaba. Sección 9 cubre el mensaje
 * específico nuevo.
 *
 * Cómo correr:
 *   node --test src/features/cancellations/components/cancelarReservaInline.test.mjs
 *
 * Nota: las funciones de lógica se replican aquí (sin importar el .jsx) porque el runner
 * es Node puro sin bundler y no puede procesar JSX ni imports de React.
 * Si cambia la lógica del componente, actualizar también estas réplicas.
 *
 * Los textos de carteles y mensajes de éxito se importan desde cancelarReservaCopy.js
 * (módulo puro .js), que es el MISMO que usa el componente. Esto ancla los tests al
 * texto real: si alguien lo edita en el módulo de copy, los tests de la sección 7 fallan.
 */

import test from "node:test";
import assert from "node:assert/strict";
import {
    TEXTO_BANNER_DIRECT_CANCEL,
    TEXTO_BANNER_SALDO_FAVOR_INICIO,
    TEXTO_BANNER_SALDO_FAVOR_ANTE_NEGRITA,
    TEXTO_BANNER_SALDO_FAVOR_NEGRITA,
    TEXTO_BANNER_SALDO_FAVOR_POST_NEGRITA,
    TEXTO_BANNER_CREDIT_NOTE,
    TEXTO_AVISO_CONTADOR_COBROS_SIN_FACTURA,
    MENSAJE_EXITO_DIRECT_CANCEL,
    MENSAJE_EXITO_PAYMENTS_TO_CREDIT,
    MENSAJE_EXITO_CREDIT_NOTE,
    TEXTO_REQUIERE_APROBACION_MULTI,
} from "./cancelarReservaCopy.js";
// Tanda 3 (2026-07-20): el mapeo código → texto criollo del 409 ahora vive en un módulo real
// (no una réplica) — se importa directo para que este archivo no mantenga una segunda copia
// que pueda divergir de lib/anularReservaRechazoLogic.test.mjs, que ya lo testea a fondo.
import { resolverTextoRechazoAnularReserva } from "../lib/anularReservaRechazoLogic.js";

// ─── Réplica de determinarCasoAnulacion (CancelarReservaInline.jsx) ───────────

/**
 * Determina el caso de anulación a partir del discriminador del backend.
 * Cuando cancellationCase no viene, cae al booleano legacy.
 */
function determinarCasoAnulacion(reserva) {
    if (reserva?.cancellationCase) {
        return reserva.cancellationCase;
    }
    return reserva?.requiresInvoiceAnnulmentToCancel === true ? "CreditNote" : "DirectCancel";
}

// ─── Réplica de formatCurrency (src/lib/utils.js) — solo ARS y USD ────────────
// Se inlinea para no depender de la cadena de imports (clsx, tailwind-merge).
// Si cambia el comportamiento de formatCurrency, actualizar esta réplica también.

function formatCurrency(amount, currency) {
    if (amount === undefined || amount === null) {
        if (!currency) return "$0.00";
        return currency === "USD" ? "US$0.00" : "$0,00";
    }
    const number = Number(amount);
    if (currency === "ARS") {
        return new Intl.NumberFormat("es-AR", {
            style: "currency",
            currency: "ARS",
            minimumFractionDigits: 2,
        }).format(number);
    }
    if (currency === "USD") {
        return "US$" + new Intl.NumberFormat("es-AR", {
            minimumFractionDigits: 2,
            maximumFractionDigits: 2,
        }).format(number);
    }
    return new Intl.NumberFormat("en-US", {
        style: "currency",
        currency: "USD",
        minimumFractionDigits: 2,
    }).format(number);
}

// ─── Réplica de formatearMontosSaldoAFavor (CancelarReservaInline.jsx) ────────

function formatearMontosSaldoAFavor(creditByCurrency) {
    if (!creditByCurrency || creditByCurrency.length === 0) return "";
    return creditByCurrency
        .map((item) => formatCurrency(item.amount, item.currency))
        .join(" · ");
}

// ─── Réplica de la lógica de routing de API ───────────────────────────────────

/**
 * Determina si el caso usa el endpoint annulWithCredit (true) o el flujo
 * draft/confirm (false). Réplica del branch en handleCancelar.
 *
 * DirectCancel y PaymentsToCredit (ambos SIN factura) van al endpoint annul-with-credit;
 * el backend decide internamente si genera saldo a favor (cuando hay cobros) o no.
 * Solo CreditNote (con factura CAE viva) usa el flujo draft/confirm que emite la NC.
 */
function usaRutaAnnulWithCredit(caso) {
    return caso === "PaymentsToCredit" || caso === "DirectCancel";
}

// ─── Réplica de la selección de mensaje de éxito ─────────────────────────────

/**
 * Devuelve el texto del toast de éxito según el caso de anulación.
 * Réplica de los showSuccess en handleCancelar. Usa las mismas constantes que
 * el componente (importadas del módulo de copy).
 *
 * Obra "anular sin factura" (2026-07-23): en PaymentsToCredit (Caso 3) el toast de éxito
 * suma SIEMPRE la línea del contador pegada abajo (separada por "\n", igual que el
 * componente real) — este caso implica por definición que hubo cobros sin factura.
 */
function elegirMensajeExito(caso) {
    if (caso === "PaymentsToCredit") {
        return `${MENSAJE_EXITO_PAYMENTS_TO_CREDIT}\n${TEXTO_AVISO_CONTADOR_COBROS_SIN_FACTURA}`;
    }
    if (caso === "DirectCancel") return MENSAJE_EXITO_DIRECT_CANCEL;
    // CreditNote y cualquier otro caso (DTOs viejos, NotApplicable, etc.)
    return MENSAJE_EXITO_CREDIT_NOTE;
}

// ─── Réplica de la lógica de cartel visible ──────────────────────────────────

/**
 * Devuelve el data-testid del cartel que debe mostrarse.
 * Réplica de los ternarios del JSX de CancelarReservaInline.
 */
function determinarCartelVisible(caso) {
    if (caso === "DirectCancel") return "cancelar-banner-sin-factura";
    if (caso === "PaymentsToCredit") return "cancelar-banner-saldo-favor";
    return "cancelar-banner-con-factura"; // CreditNote, NotApplicable, PreSale, etc.
}

// ─── Réplica de la alternancia cartel-del-caso vs cartel-de-error (P4-2) ──────

/**
 * Devuelve la lista de data-testid de carteles visibles a la vez en el estado "form".
 * Réplica de los `{!conflictMessage && ...}` del JSX: con error, el cartel del caso
 * se esconde y solo queda el de error ("cancelar-inline-conflict-msg"); sin error,
 * se ve solo el cartel del caso.
 */
function determinarCartelesVisiblesEnForm(caso, conflictMessage) {
    if (conflictMessage) return ["cancelar-inline-conflict-msg"];
    return [determinarCartelVisible(caso)];
}

// ─── Réplica del mapeo de errores para PaymentsToCredit ──────────────────────

/**
 * Mapea un error del endpoint annulWithCredit a un objeto con el tipo de
 * presentación (conflicto inline vs toast) y el mensaje amigable.
 * Réplica de la cadena if/else del catch en handleCancelar (caso PaymentsToCredit).
 *
 * ADR-042 (2026-07-01): se sacó el mensaje especial de INV-100 ("más de una factura
 * emitida... contactá a administración") — esa política vieja ya no aplica: ahora se
 * puede anular una reserva con varias facturas (ver multiCreditNoteFlow.js).
 *
 * Tanda 3 "contrato pantalla-motor" (2026-07-20): el 409 ya NO cae siempre al mismo
 * mensaje genérico — usa el mapa código → criollo real (anularReservaRechazoLogic.js),
 * que resuelve los 4 códigos ANNUL_CREDIT_* propios de este endpoint (agregados por el
 * backend en esta misma tanda) y cae al mismo texto neutro de siempre para cualquier
 * otro código (o ninguno).
 *
 * @returns {{ tipo: "conflicto"|"toast", mensaje: string }}
 */
function mapearErrorAnnulWithCredit(error) {
    if (error?.status === 400) {
        return { tipo: "conflicto", mensaje: "Revisá el motivo de la anulación (mínimo 10 caracteres)." };
    }
    if (error?.status === 403) {
        return { tipo: "toast", mensaje: "No tenés permiso para anular esta reserva." };
    }
    if (error?.status === 404) {
        return { tipo: "toast", mensaje: "No encontramos la reserva. Recargá la página." };
    }
    if (error?.status === 409) {
        const { texto } = resolverTextoRechazoAnularReserva(
            error,
            "No se pudo anular la reserva. Probá de nuevo; si el problema sigue, contactá a administración."
        );
        return { tipo: "conflicto", mensaje: texto };
    }
    return { tipo: "toast", mensaje: "No se pudo anular la reserva. Probá de nuevo en unos segundos." };
}

// ─── Réplica del mapeo de errores del flujo multi-factura (ADR-042) ──────────
// Fix bug prod (2026-07-02): 409 requiresApproval necesita un mensaje específico, no el
// genérico "probá de nuevo". Réplicas de los catch de handleConfirmarMulti y
// handleReintentarDesdeRevision (CancelarReservaInline.jsx).

/**
 * Réplica del catch de handleConfirmarMulti (Estado 1 → 2, confirma la anulación multi-factura).
 * Siempre vuelve a estadoMulti="form" (regla existente: si no se emitió ninguna nota todavía,
 * no hay "en revisión" posible — el usuario corrige y reintenta desde el formulario).
 *
 * @returns {{ estadoMulti: string, conflictMessage?: string, toast?: string }}
 */
function mapearErrorConfirmarMulti(error) {
    if (error?.status === 409 && error?.payload?.requiresApproval === true) {
        return { estadoMulti: "form", conflictMessage: TEXTO_REQUIERE_APROBACION_MULTI };
    }
    if (error?.status === 409) {
        // Tanda 3 (2026-07-20): este handler llama al mismo confirm() que el camino
        // mono-factura — mismo mapa código → criollo (INV-093/INV-100 incluidos).
        const { texto } = resolverTextoRechazoAnularReserva(
            error,
            "No se pudo confirmar la anulación. Probá de nuevo; si el problema sigue, contactá a administración."
        );
        return { estadoMulti: "form", conflictMessage: texto };
    }
    return { estadoMulti: "form", toast: "No se pudo confirmar la anulación. Probá de nuevo en unos segundos." };
}

/**
 * Réplica del catch de handleReintentarDesdeRevision (Estado 4, "Reintentar la que falta").
 * A diferencia de confirmar, el error genérico se queda en "revision-multi" (con un toast que
 * desaparece solo) — pero requiresApproval necesita "form" porque es el único estado del panel
 * donde conflictMessage tiene dónde mostrarse de forma persistente.
 *
 * NOTA: verificado contra el backend (RetryCreditNotesAsync usa requesterIsAdmin:true) que este
 * caso hoy no debería dispararse en la práctica — el reintento no vuelve a pedir aprobación,
 * solo COMPLETA lo ya autorizado al confirmar. Se deja igual por simetría y como defensa ante un
 * cambio futuro del backend.
 *
 * @returns {{ estadoMulti: string, conflictMessage?: string, toast?: string }}
 */
function mapearErrorReintentarMulti(error) {
    if (error?.status === 409 && error?.payload?.requiresApproval === true) {
        return { estadoMulti: "form", conflictMessage: TEXTO_REQUIERE_APROBACION_MULTI };
    }
    return {
        estadoMulti: "revision-multi",
        toast: "No se pudo reintentar la anulación. Probá de nuevo en unos segundos.",
    };
}

// ============================================================================
// Sección 1: determinarCasoAnulacion — fuente primaria: cancellationCase
// ============================================================================

test("caso DirectCancel → devuelve 'DirectCancel'", () => {
    const reserva = { cancellationCase: "DirectCancel" };
    assert.equal(determinarCasoAnulacion(reserva), "DirectCancel");
});

test("caso PaymentsToCredit → devuelve 'PaymentsToCredit'", () => {
    const reserva = { cancellationCase: "PaymentsToCredit" };
    assert.equal(determinarCasoAnulacion(reserva), "PaymentsToCredit");
});

test("caso CreditNote → devuelve 'CreditNote'", () => {
    const reserva = { cancellationCase: "CreditNote" };
    assert.equal(determinarCasoAnulacion(reserva), "CreditNote");
});

test("caso NotApplicable → devuelve 'NotApplicable' (este panel no debería recibir este caso)", () => {
    const reserva = { cancellationCase: "NotApplicable" };
    assert.equal(determinarCasoAnulacion(reserva), "NotApplicable");
});

test("caso PreSale → devuelve 'PreSale' (tampoco debería llegar a este panel)", () => {
    const reserva = { cancellationCase: "PreSale" };
    assert.equal(determinarCasoAnulacion(reserva), "PreSale");
});

// ─── Fallback a legacy ────────────────────────────────────────────────────────

test("sin cancellationCase + requiresInvoiceAnnulmentToCancel=true → fallback a 'CreditNote'", () => {
    const reserva = { requiresInvoiceAnnulmentToCancel: true };
    assert.equal(determinarCasoAnulacion(reserva), "CreditNote");
});

test("sin cancellationCase + requiresInvoiceAnnulmentToCancel=false → fallback a 'DirectCancel'", () => {
    const reserva = { requiresInvoiceAnnulmentToCancel: false };
    assert.equal(determinarCasoAnulacion(reserva), "DirectCancel");
});

test("sin cancellationCase + sin requiresInvoiceAnnulmentToCancel → fallback a 'DirectCancel' (conservador)", () => {
    const reserva = {};
    assert.equal(determinarCasoAnulacion(reserva), "DirectCancel");
});

test("reserva null → 'DirectCancel' (no rompe)", () => {
    assert.equal(determinarCasoAnulacion(null), "DirectCancel");
});

test("cancellationCase string no vacío siempre tiene precedencia sobre requiresInvoiceAnnulmentToCancel", () => {
    const reserva = {
        cancellationCase: "DirectCancel",
        requiresInvoiceAnnulmentToCancel: true,
    };
    assert.equal(determinarCasoAnulacion(reserva), "DirectCancel");
});

// ============================================================================
// Sección 2: formatearMontosSaldoAFavor — formateo de montos para el cartel celeste
// ============================================================================

test("array vacío → string vacío", () => {
    assert.equal(formatearMontosSaldoAFavor([]), "");
});

test("null → string vacío (no rompe)", () => {
    assert.equal(formatearMontosSaldoAFavor(null), "");
});

test("undefined → string vacío (no rompe)", () => {
    assert.equal(formatearMontosSaldoAFavor(undefined), "");
});

test("un solo monto ARS → contiene '$' y el número formateado", () => {
    const result = formatearMontosSaldoAFavor([{ currency: "ARS", amount: 150000 }]);
    assert.match(result, /\$/);
    assert.match(result, /150/);
    assert.ok(!result.includes(" · "), "No debe tener separador cuando es un solo monto");
});

test("un solo monto USD → empieza con 'US$' y contiene el número", () => {
    const result = formatearMontosSaldoAFavor([{ currency: "USD", amount: 200 }]);
    assert.ok(result.startsWith("US$"), `Esperado 'US$...' pero recibí: ${result}`);
    assert.match(result, /200/);
    assert.ok(!result.includes(" · "));
});

test("ARS + USD → separados por ' · ' (nunca sumados)", () => {
    const result = formatearMontosSaldoAFavor([
        { currency: "ARS", amount: 150000 },
        { currency: "USD", amount: 200 },
    ]);
    assert.ok(result.includes(" · "), `El separador ' · ' debe estar presente: ${result}`);
    const parts = result.split(" · ");
    assert.equal(parts.length, 2, "Debe haber exactamente 2 partes");
    assert.match(parts[0], /\$/);
    assert.match(parts[1], /US\$/);
});

test("USD + ARS (orden inverso) → mantiene el orden del array del backend", () => {
    const result = formatearMontosSaldoAFavor([
        { currency: "USD", amount: 500 },
        { currency: "ARS", amount: 80000 },
    ]);
    const parts = result.split(" · ");
    assert.equal(parts.length, 2);
    assert.ok(parts[0].startsWith("US$"), "El primero debe ser USD");
    assert.match(parts[1], /\$/);
});

test("monto ARS 0 → muestra '$0' (no omite montos de cero)", () => {
    const result = formatearMontosSaldoAFavor([{ currency: "ARS", amount: 0 }]);
    assert.match(result, /\$/);
    assert.match(result, /0/);
});

// ============================================================================
// Sección 3: routing de API según el caso
// ============================================================================

test("PaymentsToCredit → usa annulWithCredit (NO draft/confirm)", () => {
    assert.equal(usaRutaAnnulWithCredit("PaymentsToCredit"), true);
});

test("DirectCancel → usa annulWithCredit (NO draft/confirm: el endpoint sin factura hace la baja directa)", () => {
    assert.equal(usaRutaAnnulWithCredit("DirectCancel"), true);
});

test("CreditNote → usa draft/confirm (NO annulWithCredit)", () => {
    assert.equal(usaRutaAnnulWithCredit("CreditNote"), false);
});

test("NotApplicable → usa draft/confirm (fallback conservador)", () => {
    assert.equal(usaRutaAnnulWithCredit("NotApplicable"), false);
});

test("caso desconocido futuro → usa draft/confirm (no asume annulWithCredit)", () => {
    assert.equal(usaRutaAnnulWithCredit("AlgunCasoNuevo"), false);
});

// ============================================================================
// Sección 4: mensajes de éxito por caso (importados del módulo de copy)
// ============================================================================

test("DirectCancel: mensaje de éxito = 'Reserva anulada.' (sin mención a NC)", () => {
    assert.equal(elegirMensajeExito("DirectCancel"), "Reserva anulada.");
});

test("PaymentsToCredit: mensaje de éxito confirma que la plata quedó como saldo a favor + línea del contador debajo", () => {
    const mensaje = elegirMensajeExito("PaymentsToCredit");
    assert.equal(
        mensaje,
        "Reserva anulada. Lo cobrado quedó como saldo a favor del cliente.\nHubo cobros sin factura: revisalo con tu contador."
    );
    assert.ok(!mensaje.includes("nota de crédito"), "No debe mencionar NC en el caso PaymentsToCredit");
    // El mensaje del operador (reembolso) NUNCA se menciona en el confirm/éxito de anular
    // reserva (P2=A de la obra 2026-07-23): vive en la cuenta del operador, no acá.
    assert.ok(!mensaje.toLowerCase().includes("operador"), "No debe mencionar al operador");
});

// Obra "anular sin factura" (2026-07-23, T-6): fija con Assert/equal exacto el texto nuevo
// de la línea del contador, tanto solo como dentro del mensaje de éxito combinado.
test("TEXTO_AVISO_CONTADOR_COBROS_SIN_FACTURA = texto exacto firmado por Gastón", () => {
    assert.equal(
        TEXTO_AVISO_CONTADOR_COBROS_SIN_FACTURA,
        "Hubo cobros sin factura: revisalo con tu contador."
    );
});

test("DirectCancel (Caso a): el mensaje de éxito NO lleva la línea del contador (sin cobros, no aplica)", () => {
    const mensaje = elegirMensajeExito("DirectCancel");
    assert.equal(mensaje, "Reserva anulada.");
    assert.ok(!mensaje.includes(TEXTO_AVISO_CONTADOR_COBROS_SIN_FACTURA));
});

test("CreditNote (con factura): el mensaje de éxito NO lleva la línea del contador (caso ámbar, sin cambios)", () => {
    const mensaje = elegirMensajeExito("CreditNote");
    assert.ok(!mensaje.includes(TEXTO_AVISO_CONTADOR_COBROS_SIN_FACTURA));
});

test("CreditNote: mensaje de éxito menciona la nota de crédito en generación", () => {
    assert.equal(elegirMensajeExito("CreditNote"), "Reserva anulada. La nota de crédito se está generando.");
});

test("caso desconocido → mismo mensaje que CreditNote (fallback conservador)", () => {
    assert.equal(elegirMensajeExito("AlgunCasoNuevo"), "Reserva anulada. La nota de crédito se está generando.");
});

test("los 3 mensajes de éxito contienen 'Reserva anulada' (vocabulario ADR-036)", () => {
    for (const caso of ["DirectCancel", "PaymentsToCredit", "CreditNote"]) {
        const mensaje = elegirMensajeExito(caso);
        assert.ok(mensaje.startsWith("Reserva anulada"), `Caso ${caso}: mensaje debe empezar con 'Reserva anulada'`);
    }
});

// ============================================================================
// Sección 5: visibilidad de carteles por caso
// ============================================================================

test("DirectCancel → muestra banner verde (data-testid='cancelar-banner-sin-factura')", () => {
    assert.equal(determinarCartelVisible("DirectCancel"), "cancelar-banner-sin-factura");
});

test("PaymentsToCredit → muestra banner celeste (data-testid='cancelar-banner-saldo-favor')", () => {
    assert.equal(determinarCartelVisible("PaymentsToCredit"), "cancelar-banner-saldo-favor");
});

test("CreditNote → muestra banner ámbar (data-testid='cancelar-banner-con-factura')", () => {
    assert.equal(determinarCartelVisible("CreditNote"), "cancelar-banner-con-factura");
});

test("NotApplicable → muestra banner ámbar (fallback seguro)", () => {
    assert.equal(determinarCartelVisible("NotApplicable"), "cancelar-banner-con-factura");
});

test("PreSale → muestra banner ámbar (no debería llegar acá, fallback seguro)", () => {
    assert.equal(determinarCartelVisible("PreSale"), "cancelar-banner-con-factura");
});

test("caso vacío → muestra banner ámbar (extra defensivo)", () => {
    assert.equal(determinarCartelVisible(""), "cancelar-banner-con-factura");
});

// ============================================================================
// Sección 6: coherencia entre discriminador, cartel y mensaje
// ============================================================================

test("DirectCancel: coherencia caso → cartel verde + mensaje corto + ruta annulWithCredit", () => {
    const caso = "DirectCancel";
    assert.equal(determinarCartelVisible(caso), "cancelar-banner-sin-factura");
    assert.equal(elegirMensajeExito(caso), "Reserva anulada.");
    assert.equal(usaRutaAnnulWithCredit(caso), true);
});

test("PaymentsToCredit: coherencia caso → cartel celeste + mensaje saldo a favor + ruta annulWithCredit", () => {
    const caso = "PaymentsToCredit";
    assert.equal(determinarCartelVisible(caso), "cancelar-banner-saldo-favor");
    assert.match(elegirMensajeExito(caso), /saldo a favor/i);
    assert.equal(usaRutaAnnulWithCredit(caso), true);
});

test("CreditNote: coherencia caso → cartel ámbar + mensaje NC generando + ruta draft/confirm", () => {
    const caso = "CreditNote";
    assert.equal(determinarCartelVisible(caso), "cancelar-banner-con-factura");
    assert.match(elegirMensajeExito(caso), /nota de crédito/i);
    assert.equal(usaRutaAnnulWithCredit(caso), false);
});

// ============================================================================
// Sección 7: textos anclados a la guía UX (importados del módulo de copy)
//
// Estos tests fallan si alguien edita una constante en cancelarReservaCopy.js
// sin actualizar la guía UX primero. Son la "fuente de verdad" del texto visible.
// ============================================================================

test("TEXTO_BANNER_DIRECT_CANCEL = texto exacto de la guía UX 2026-06-25", () => {
    assert.equal(
        TEXTO_BANNER_DIRECT_CANCEL,
        "Esta reserva no tiene factura emitida, se anula directo, sin nota de crédito."
    );
});

test("TEXTO_BANNER_CREDIT_NOTE = texto exacto de la guía UX 2026-06-25", () => {
    assert.equal(
        TEXTO_BANNER_CREDIT_NOTE,
        "Esta reserva tiene factura emitida, al anular se emite la nota de crédito en AFIP/ARCA para anularla."
    );
});

test("TEXTO_BANNER_SALDO_FAVOR_INICIO = texto exacto de la guía UX 2026-06-25", () => {
    assert.equal(
        TEXTO_BANNER_SALDO_FAVOR_INICIO,
        "Esta reserva no tiene factura, pero el cliente ya pagó"
    );
});

test("TEXTO_BANNER_SALDO_FAVOR_NEGRITA = 'SALDO A FAVOR' (en mayúsculas, como la guía)", () => {
    assert.equal(TEXTO_BANNER_SALDO_FAVOR_NEGRITA, "SALDO A FAVOR");
});

test("partes del banner celeste reconstituyen el texto exacto de la guía UX", () => {
    // Verifica que al combinar las 4 partes con un monto de ejemplo, el resultado
    // coincide palabra por palabra con lo que dice la guía UX.
    const montoEjemplo = "$ 150.000";
    const textoReconstituido =
        TEXTO_BANNER_SALDO_FAVOR_INICIO + ` (${montoEjemplo}). ` +
        TEXTO_BANNER_SALDO_FAVOR_ANTE_NEGRITA +
        TEXTO_BANNER_SALDO_FAVOR_NEGRITA +
        TEXTO_BANNER_SALDO_FAVOR_POST_NEGRITA;

    const textoGuia =
        `Esta reserva no tiene factura, pero el cliente ya pagó (${montoEjemplo}). ` +
        "Al anular, esos montos quedan como SALDO A FAVOR del cliente, para usar en otra reserva.";

    assert.equal(textoReconstituido, textoGuia);
});

// Obra "anular sin factura" (2026-07-23, T-6): el cartel de CONFIRMAR (Caso 3) es el texto
// firmado de siempre + la línea del contador pegada abajo, dentro del MISMO cartel.
test("cartel de confirmar (Caso 3): banner celeste + línea del contador pegada abajo, texto exacto", () => {
    const montoEjemplo = "$ 150.000";
    const bannerCeleste =
        TEXTO_BANNER_SALDO_FAVOR_INICIO + ` (${montoEjemplo}). ` +
        TEXTO_BANNER_SALDO_FAVOR_ANTE_NEGRITA +
        TEXTO_BANNER_SALDO_FAVOR_NEGRITA +
        TEXTO_BANNER_SALDO_FAVOR_POST_NEGRITA;

    // El componente real renderiza ambas líneas como dos <span> DENTRO del mismo <div> del
    // cartel (mismo bloque semántico) — acá concatenamos con un separador para comparar el
    // contenido de texto, no el markup.
    const textoCompletoDelCartel = `${bannerCeleste} ${TEXTO_AVISO_CONTADOR_COBROS_SIN_FACTURA}`;

    assert.ok(textoCompletoDelCartel.includes("SALDO A FAVOR"));
    assert.ok(textoCompletoDelCartel.includes("Hubo cobros sin factura: revisalo con tu contador."));
});

test("cartel de confirmar (Caso a, DirectCancel): NO lleva la línea del contador (sin cobros, no aplica)", () => {
    assert.ok(!TEXTO_BANNER_DIRECT_CANCEL.includes("contador"));
});

test("MENSAJE_EXITO_DIRECT_CANCEL = texto exacto de la guía UX 2026-06-25", () => {
    assert.equal(MENSAJE_EXITO_DIRECT_CANCEL, "Reserva anulada.");
});

test("MENSAJE_EXITO_PAYMENTS_TO_CREDIT = texto exacto de la guía UX 2026-06-25", () => {
    assert.equal(
        MENSAJE_EXITO_PAYMENTS_TO_CREDIT,
        "Reserva anulada. Lo cobrado quedó como saldo a favor del cliente."
    );
});

test("MENSAJE_EXITO_CREDIT_NOTE = texto exacto de la guía UX 2026-06-25", () => {
    assert.equal(
        MENSAJE_EXITO_CREDIT_NOTE,
        "Reserva anulada. La nota de crédito se está generando."
    );
});

// ============================================================================
// Sección 8: mapeo de errores del endpoint annulWithCredit (caso PaymentsToCredit)
//
// 400 y 409 son inline (conflicto, el usuario puede corregir en el formulario).
// 403 y 404 son toasts (no hay acción que el usuario pueda tomar en el formulario).
// ============================================================================

test("error 400 → inline, mensaje de motivo inválido", () => {
    // 400: el backend rechazó el motivo aunque el front lo validó (double-check server-side).
    const r = mapearErrorAnnulWithCredit({ status: 400 });
    assert.equal(r.tipo, "conflicto");
    assert.match(r.mensaje, /motivo/i);
    assert.match(r.mensaje, /10 caracteres/i);
});

test("error 403 → toast, mensaje de sin permiso", () => {
    // 403: el usuario no tiene permiso. No tiene sentido mostrarlo inline.
    const r = mapearErrorAnnulWithCredit({ status: 403 });
    assert.equal(r.tipo, "toast");
    assert.match(r.mensaje, /permiso/i);
});

test("error 404 → toast, mensaje de reserva no encontrada + instrucción de recarga", () => {
    const r = mapearErrorAnnulWithCredit({ status: 404 });
    assert.equal(r.tipo, "toast");
    assert.match(r.mensaje, /no encontramos la reserva/i);
    assert.match(r.mensaje, /recargá/i);
});

test("error 409 con un código ANNUL_CREDIT_* mapeado (ej. reserva no firme) → mensaje real de la Tanda 3, no el genérico", () => {
    const r = mapearErrorAnnulWithCredit({ status: 409, payload: { code: "ANNUL_CREDIT_NOT_FIRM_STATE" } });
    assert.equal(r.tipo, "conflicto");
    assert.match(r.mensaje, /En gestión ni Confirmada/);
});

test("error 409 sin ningún código catalogado → mensaje neutro genérico (fallback, política sin cambios)", () => {
    // ADR-042: ya no existe el mensaje especial de INV-100 mencionando "más de una factura" ni
    // una solapa inexistente. Un código sin catalogar sigue cayendo al genérico de siempre.
    const r = mapearErrorAnnulWithCredit({ status: 409, payload: { code: "ALGO_SIN_CATALOGAR" } });
    assert.equal(r.tipo, "conflicto");
    assert.equal(r.mensaje, "No se pudo anular la reserva. Probá de nuevo; si el problema sigue, contactá a administración.");
    assert.ok(!/más de una factura/i.test(r.mensaje));
    assert.ok(!/solapa Facturas/i.test(r.mensaje));
});

test("error 409 genérico (sin payload) → inline, mensaje de 'no se pudo anular'", () => {
    const r = mapearErrorAnnulWithCredit({ status: 409, payload: {} });
    assert.equal(r.tipo, "conflicto");
    assert.match(r.mensaje, /no se pudo anular/i);
});

test("error 500/otro → toast, mensaje genérico de reintento", () => {
    const r = mapearErrorAnnulWithCredit({ status: 500 });
    assert.equal(r.tipo, "toast");
    assert.match(r.mensaje, /probá de nuevo/i);
});

test("error undefined (network error) → toast, mensaje genérico", () => {
    const r = mapearErrorAnnulWithCredit(undefined);
    assert.equal(r.tipo, "toast");
    assert.match(r.mensaje, /probá de nuevo/i);
});

test("error 400/403/404 nunca tienen el mismo mensaje (no se confunden entre sí)", () => {
    const e400 = mapearErrorAnnulWithCredit({ status: 400 }).mensaje;
    const e403 = mapearErrorAnnulWithCredit({ status: 403 }).mensaje;
    const e404 = mapearErrorAnnulWithCredit({ status: 404 }).mensaje;
    assert.notEqual(e400, e403);
    assert.notEqual(e400, e404);
    assert.notEqual(e403, e404);
});

// ============================================================================
// Sección 9: mapeo de errores del flujo multi-factura (confirmar y reintentar)
//
// Fix bug prod (2026-07-02): 409 requiresApproval necesita un mensaje específico que le
// diga al usuario qué pasa y qué hacer — el genérico "probá de nuevo" no servía.
// ============================================================================

test("confirmarMulti: 409 requiresApproval=true → vuelve a 'form' con el mensaje específico", () => {
    const r = mapearErrorConfirmarMulti({ status: 409, payload: { requiresApproval: true } });
    assert.equal(r.estadoMulti, "form");
    assert.equal(r.conflictMessage, TEXTO_REQUIERE_APROBACION_MULTI);
    assert.equal(r.toast, undefined);
});

test("confirmarMulti: mensaje de requiresApproval no expone requestType/entityType/entityId ni códigos internos", () => {
    // Data-exposure: el mensaje es fijo y neutro, nunca arma el texto a partir del payload crudo.
    assert.ok(!/requestType|entityType|entityId/i.test(TEXTO_REQUIERE_APROBACION_MULTI));
    assert.ok(!/INV-|[0-9a-f]{8}-[0-9a-f]{4}/i.test(TEXTO_REQUIERE_APROBACION_MULTI));
});

test("confirmarMulti: 409 sin requiresApproval → mensaje genérico de confirmar (no el de requiresApproval)", () => {
    const r = mapearErrorConfirmarMulti({ status: 409, payload: {} });
    assert.equal(r.estadoMulti, "form");
    assert.match(r.conflictMessage, /no se pudo confirmar/i);
    assert.notEqual(r.conflictMessage, TEXTO_REQUIERE_APROBACION_MULTI);
});

test("confirmarMulti: 409 con INV-093 → usa el mismo mapa código → criollo que el camino mono-factura (Tanda 3)", () => {
    const r = mapearErrorConfirmarMulti({ status: 409, payload: { code: "business_invariant_violation", invariantCode: "INV-093" } });
    assert.equal(r.estadoMulti, "form");
    assert.match(r.conflictMessage, /cambió de estado mientras la tenías abierta/);
});

test("confirmarMulti: requiresApproval=true pero status !== 409 → NO dispara el mensaje específico (degradación segura)", () => {
    const r = mapearErrorConfirmarMulti({ status: 500, payload: { requiresApproval: true } });
    assert.notEqual(r.conflictMessage, TEXTO_REQUIERE_APROBACION_MULTI);
});

test("confirmarMulti: error sin status (network error) → toast genérico, no conflictMessage", () => {
    const r = mapearErrorConfirmarMulti(undefined);
    assert.equal(r.estadoMulti, "form");
    assert.equal(r.conflictMessage, undefined);
    assert.match(r.toast, /probá de nuevo/i);
});

test("reintentarMulti: 409 requiresApproval=true → vuelve a 'form' con el mensaje específico (no se queda en revision-multi)", () => {
    const r = mapearErrorReintentarMulti({ status: 409, payload: { requiresApproval: true } });
    assert.equal(r.estadoMulti, "form");
    assert.equal(r.conflictMessage, TEXTO_REQUIERE_APROBACION_MULTI);
});

test("reintentarMulti: error genérico → se queda en 'revision-multi' con toast (el usuario puede reintentar de nuevo ahí mismo)", () => {
    const r = mapearErrorReintentarMulti({ status: 500 });
    assert.equal(r.estadoMulti, "revision-multi");
    assert.match(r.toast, /no se pudo reintentar/i);
    assert.equal(r.conflictMessage, undefined);
});

test("reintentarMulti: 409 sin requiresApproval → igual que cualquier otro error, se queda en revision-multi", () => {
    const r = mapearErrorReintentarMulti({ status: 409, payload: {} });
    assert.equal(r.estadoMulti, "revision-multi");
    assert.notEqual(r.toast, undefined);
});

test("confirmarMulti y reintentarMulti dan el MISMO conflictMessage para requiresApproval (mensaje único, sin divergencia)", () => {
    const a = mapearErrorConfirmarMulti({ status: 409, payload: { requiresApproval: true } });
    const b = mapearErrorReintentarMulti({ status: 409, payload: { requiresApproval: true } });
    assert.equal(a.conflictMessage, b.conflictMessage);
});

// ============================================================================
// Sección 10: alternancia cartel del caso vs cartel de error (P4-2)
//
// Antes convivían hasta 2 carteles en el estado "form" (el del caso + el de error).
// Ahora es alternancia: nunca los dos juntos.
// ============================================================================

test("sin error → se ve solo el cartel del caso (DirectCancel)", () => {
    const visibles = determinarCartelesVisiblesEnForm("DirectCancel", null);
    assert.deepEqual(visibles, ["cancelar-banner-sin-factura"]);
});

test("sin error → se ve solo el cartel del caso (PaymentsToCredit)", () => {
    const visibles = determinarCartelesVisiblesEnForm("PaymentsToCredit", null);
    assert.deepEqual(visibles, ["cancelar-banner-saldo-favor"]);
});

test("sin error → se ve solo el cartel del caso (CreditNote)", () => {
    const visibles = determinarCartelesVisiblesEnForm("CreditNote", null);
    assert.deepEqual(visibles, ["cancelar-banner-con-factura"]);
});

test("con error → se ve SOLO el cartel de error, nunca el del caso (DirectCancel)", () => {
    const visibles = determinarCartelesVisiblesEnForm("DirectCancel", "No se pudo anular: motivo X.");
    assert.deepEqual(visibles, ["cancelar-inline-conflict-msg"]);
    assert.ok(!visibles.includes("cancelar-banner-sin-factura"), "el cartel del caso no debe convivir con el de error");
});

test("con error → se ve SOLO el cartel de error, nunca el del caso (CreditNote, el caso ámbar más frecuente con error)", () => {
    const visibles = determinarCartelesVisiblesEnForm("CreditNote", "El operador ya cobró y no hay factura para anclar el reembolso.");
    assert.deepEqual(visibles, ["cancelar-inline-conflict-msg"]);
    assert.ok(!visibles.includes("cancelar-banner-con-factura"), "el cartel ámbar no debe convivir con el de error");
});

test("nunca hay más de un cartel visible a la vez en el estado form, sea cual sea el caso o el error", () => {
    for (const caso of ["DirectCancel", "PaymentsToCredit", "CreditNote", "NotApplicable", ""]) {
        for (const conflictMessage of [null, undefined, "", "algún error"]) {
            const visibles = determinarCartelesVisiblesEnForm(caso, conflictMessage);
            assert.equal(visibles.length, 1, `caso=${caso} conflictMessage=${JSON.stringify(conflictMessage)} debe mostrar exactamente 1 cartel`);
        }
    }
});

test("el error se limpia (conflictMessage vuelve a null/undefined) → el cartel del caso reaparece intacto", () => {
    // Simula el ciclo: error → el vendedor corrige → setConflictMessage(null) (líneas 179/382
    // del componente) → vuelve a verse el cartel del caso, sin perder información del caso.
    const conError = determinarCartelesVisiblesEnForm("PaymentsToCredit", "motivo muy corto");
    const sinError = determinarCartelesVisiblesEnForm("PaymentsToCredit", null);
    assert.deepEqual(conError, ["cancelar-inline-conflict-msg"]);
    assert.deepEqual(sinError, ["cancelar-banner-saldo-favor"], "el cartel del caso debe reaparecer igual que antes del error");
});
