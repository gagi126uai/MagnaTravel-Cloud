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
    MENSAJE_EXITO_DIRECT_CANCEL,
    MENSAJE_EXITO_PAYMENTS_TO_CREDIT,
    MENSAJE_EXITO_CREDIT_NOTE,
} from "./cancelarReservaCopy.js";

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
 */
function usaRutaAnnulWithCredit(caso) {
    return caso === "PaymentsToCredit";
}

// ─── Réplica de la selección de mensaje de éxito ─────────────────────────────

/**
 * Devuelve el texto del toast de éxito según el caso de anulación.
 * Réplica de los showSuccess en handleCancelar. Usa las mismas constantes que
 * el componente (importadas del módulo de copy).
 */
function elegirMensajeExito(caso) {
    if (caso === "PaymentsToCredit") return MENSAJE_EXITO_PAYMENTS_TO_CREDIT;
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

// ─── Réplica del mapeo de errores para PaymentsToCredit ──────────────────────

/**
 * Mapea un error del endpoint annulWithCredit a un objeto con el tipo de
 * presentación (conflicto inline vs toast) y el mensaje amigable.
 * Réplica de la cadena if/else del catch en handleCancelar (caso PaymentsToCredit).
 *
 * @returns {{ tipo: "conflicto"|"toast", mensaje: string }}
 */
function mapearErrorAnnulWithCredit(error) {
    const code = error?.payload?.invariantCode || error?.payload?.code || "";
    if (error?.status === 400) {
        return { tipo: "conflicto", mensaje: "Revisá el motivo de la anulación (mínimo 10 caracteres)." };
    }
    if (error?.status === 403) {
        return { tipo: "toast", mensaje: "No tenés permiso para anular esta reserva." };
    }
    if (error?.status === 404) {
        return { tipo: "toast", mensaje: "No encontramos la reserva. Recargá la página." };
    }
    if (error?.status === 409 && code === "INV-100") {
        return {
            tipo: "conflicto",
            mensaje: "Esta reserva tiene más de una factura emitida. Por ahora no se puede anular toda la reserva de una vez: anulá cada factura desde la solapa Facturas, o contactá a administración.",
        };
    }
    if (error?.status === 409) {
        return {
            tipo: "conflicto",
            mensaje: "No se pudo anular la reserva. Probá de nuevo; si el problema sigue, contactá a administración.",
        };
    }
    return { tipo: "toast", mensaje: "No se pudo anular la reserva. Probá de nuevo en unos segundos." };
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

test("DirectCancel → usa draft/confirm (NO annulWithCredit)", () => {
    assert.equal(usaRutaAnnulWithCredit("DirectCancel"), false);
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

test("PaymentsToCredit: mensaje de éxito confirma que la plata quedó como saldo a favor", () => {
    const mensaje = elegirMensajeExito("PaymentsToCredit");
    assert.equal(mensaje, "Reserva anulada. Lo cobrado quedó como saldo a favor del cliente.");
    assert.ok(!mensaje.includes("nota de crédito"), "No debe mencionar NC en el caso PaymentsToCredit");
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

test("DirectCancel: coherencia caso → cartel verde + mensaje corto + ruta draft/confirm", () => {
    const caso = "DirectCancel";
    assert.equal(determinarCartelVisible(caso), "cancelar-banner-sin-factura");
    assert.equal(elegirMensajeExito(caso), "Reserva anulada.");
    assert.equal(usaRutaAnnulWithCredit(caso), false);
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

test("error 409 INV-100 (múltiples facturas) → inline, menciona 'solapa Facturas'", () => {
    const r = mapearErrorAnnulWithCredit({ status: 409, payload: { invariantCode: "INV-100" } });
    assert.equal(r.tipo, "conflicto");
    assert.match(r.mensaje, /factura/i);
});

test("error 409 genérico → inline, mensaje de 'no se pudo anular'", () => {
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
