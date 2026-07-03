/**
 * Tests de multiCreditNoteFlow.js — lógica pura del flujo de anulación con VARIAS facturas
 * en distintas monedas (ADR-042, 2026-07-01). Spec: docs/ux/2026-07-01-anulacion-multifactura.md.
 *
 * Cómo correr:
 *   node --test src/features/cancellations/lib/multiCreditNoteFlow.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import {
    esAnulacionMultiFactura,
    contarNotasFaltantes,
    contarNotasResueltas,
    todasLasNotasSalieronBien,
    algunaNotaFallo,
    hayNotaPendiente,
    etiquetaMonedaSimbolo,
    formatearResumenMonedas,
    construirTextoAvisoMultiFactura,
    construirTextoConfirmacionMulti,
    etiquetaNotaCredito,
    estadoVisualNota,
    construirTextoExitoMulti,
    construirTextoEncabezadoRevision,
    construirTextoFranjaEnRevision,
    entradasSaldoAFavor,
} from "./multiCreditNoteFlow.js";

// ============================================================================
// esAnulacionMultiFactura
// ============================================================================

test("0 facturas → no es multi-factura", () => {
    assert.equal(esAnulacionMultiFactura([]), false);
});

test("1 factura → no es multi-factura (regresión cero: sigue el flujo mono-factura)", () => {
    assert.equal(esAnulacionMultiFactura([{ currency: "ARS", amount: 100 }]), false);
});

test("2 facturas → es multi-factura", () => {
    assert.equal(esAnulacionMultiFactura([{ currency: "ARS" }, { currency: "USD" }]), true);
});

test("3 facturas → es multi-factura", () => {
    assert.equal(esAnulacionMultiFactura([{}, {}, {}]), true);
});

test("undefined/null → no es multi-factura (no rompe)", () => {
    assert.equal(esAnulacionMultiFactura(undefined), false);
    assert.equal(esAnulacionMultiFactura(null), false);
});

// ============================================================================
// contarNotasFaltantes / contarNotasResueltas
// ============================================================================

test("contarNotasFaltantes: cuenta Pending + Failed, no cuenta Succeeded", () => {
    const notas = [{ status: "Succeeded" }, { status: "Pending" }, { status: "Failed" }];
    assert.equal(contarNotasFaltantes(notas), 2);
});

test("contarNotasFaltantes: todas Succeeded → 0", () => {
    assert.equal(contarNotasFaltantes([{ status: "Succeeded" }, { status: "Succeeded" }]), 0);
});

test("contarNotasFaltantes: array vacío/null → 0", () => {
    assert.equal(contarNotasFaltantes([]), 0);
    assert.equal(contarNotasFaltantes(null), 0);
});

test("contarNotasResueltas: cuenta Succeeded + Failed, no cuenta Pending", () => {
    const notas = [{ status: "Succeeded" }, { status: "Pending" }, { status: "Failed" }];
    assert.equal(contarNotasResueltas(notas), 2);
});

test("contarNotasResueltas: todas Pending → 0", () => {
    assert.equal(contarNotasResueltas([{ status: "Pending" }, { status: "Pending" }]), 0);
});

// ============================================================================
// todasLasNotasSalieronBien / algunaNotaFallo / hayNotaPendiente
// ============================================================================

test("todasLasNotasSalieronBien: todas Succeeded → true", () => {
    assert.equal(todasLasNotasSalieronBien([{ status: "Succeeded" }, { status: "Succeeded" }]), true);
});

test("todasLasNotasSalieronBien: una Pending → false", () => {
    assert.equal(todasLasNotasSalieronBien([{ status: "Succeeded" }, { status: "Pending" }]), false);
});

test("todasLasNotasSalieronBien: una Failed → false", () => {
    assert.equal(todasLasNotasSalieronBien([{ status: "Succeeded" }, { status: "Failed" }]), false);
});

test("todasLasNotasSalieronBien: array vacío → false (nada se emitió)", () => {
    assert.equal(todasLasNotasSalieronBien([]), false);
});

test("algunaNotaFallo: hay una Failed → true", () => {
    assert.equal(algunaNotaFallo([{ status: "Succeeded" }, { status: "Failed" }]), true);
});

test("algunaNotaFallo: ninguna Failed → false", () => {
    assert.equal(algunaNotaFallo([{ status: "Succeeded" }, { status: "Pending" }]), false);
});

test("hayNotaPendiente: hay una Pending → true", () => {
    assert.equal(hayNotaPendiente([{ status: "Succeeded" }, { status: "Pending" }]), true);
});

test("hayNotaPendiente: ninguna Pending → false", () => {
    assert.equal(hayNotaPendiente([{ status: "Succeeded" }, { status: "Failed" }]), false);
});

// ============================================================================
// etiquetaMonedaSimbolo
// ============================================================================

test("etiquetaMonedaSimbolo: ARS → '$'", () => {
    assert.equal(etiquetaMonedaSimbolo("ARS"), "$");
});

test("etiquetaMonedaSimbolo: USD → 'US$'", () => {
    assert.equal(etiquetaMonedaSimbolo("USD"), "US$");
});

test("etiquetaMonedaSimbolo: moneda desconocida → devuelve el código tal cual", () => {
    assert.equal(etiquetaMonedaSimbolo("EUR"), "EUR");
});

// ============================================================================
// formatearResumenMonedas — nunca suma $ + US$ (regla dura multimoneda)
// ============================================================================

test("2 facturas, 1 ARS + 1 USD → '(una en $ y una en US$)' (copy exacto de la spec)", () => {
    const items = [{ currency: "ARS" }, { currency: "USD" }];
    assert.equal(formatearResumenMonedas(items), "(una en $ y una en US$)");
});

test("orden inverso (USD primero) → respeta el orden de aparición", () => {
    const items = [{ currency: "USD" }, { currency: "ARS" }];
    assert.equal(formatearResumenMonedas(items), "(una en US$ y una en $)");
});

test("3 facturas todas en USD → '(3 facturas en US$)'", () => {
    const items = [{ currency: "USD" }, { currency: "USD" }, { currency: "USD" }];
    assert.equal(formatearResumenMonedas(items), "(3 facturas en US$)");
});

test("2 facturas todas en ARS → '(2 facturas en $)'", () => {
    const items = [{ currency: "ARS" }, { currency: "ARS" }];
    assert.equal(formatearResumenMonedas(items), "(2 facturas en $)");
});

test("array vacío → string vacío", () => {
    assert.equal(formatearResumenMonedas([]), "");
    assert.equal(formatearResumenMonedas(null), "");
});

test("2 ARS + 1 USD → '(2 en $ y una en US$)'", () => {
    const items = [{ currency: "ARS" }, { currency: "ARS" }, { currency: "USD" }];
    assert.equal(formatearResumenMonedas(items), "(2 en $ y una en US$)");
});

test("nunca aparece un número que sume las dos monedas juntas", () => {
    const items = [{ currency: "ARS" }, { currency: "ARS" }, { currency: "USD" }];
    const resultado = formatearResumenMonedas(items);
    // El total (3) no debe aparecer como si fuera "3 en algo" — cada moneda cuenta la suya.
    assert.ok(!resultado.includes("3 en"), `No debe mezclar el total de ambas monedas: ${resultado}`);
});

// ============================================================================
// construirTextoAvisoMultiFactura (Estado 0)
// ============================================================================

test("Estado 0: copy exacto con 2 facturas en $ y US$", () => {
    const saleInvoices = [{ currency: "ARS" }, { currency: "USD" }];
    assert.equal(
        construirTextoAvisoMultiFactura(saleInvoices),
        "Esta reserva tiene 2 facturas emitidas (una en $ y una en US$). Al anular se emite una " +
        "nota de crédito por cada factura, cada una en su moneda."
    );
});

test("Estado 0: 3 facturas todas en USD", () => {
    const saleInvoices = [{ currency: "USD" }, { currency: "USD" }, { currency: "USD" }];
    assert.equal(
        construirTextoAvisoMultiFactura(saleInvoices),
        "Esta reserva tiene 3 facturas emitidas (3 facturas en US$). Al anular se emite una " +
        "nota de crédito por cada factura, cada una en su moneda."
    );
});

test("Estado 0: nunca menciona la solapa Facturas inexistente", () => {
    const saleInvoices = [{ currency: "ARS" }, { currency: "USD" }];
    assert.ok(!/solapa Facturas/i.test(construirTextoAvisoMultiFactura(saleInvoices)));
});

// ============================================================================
// construirTextoConfirmacionMulti (Estado 1 — "¿Seguro?")
// ============================================================================

test("Estado 1: copy exacto con 2 notas en $ y US$", () => {
    const saleInvoices = [{ currency: "ARS" }, { currency: "USD" }];
    assert.equal(
        construirTextoConfirmacionMulti(saleInvoices),
        "Se van a emitir 2 notas de crédito en AFIP (una en $ y una en US$). Una vez emitidas no " +
        "se pueden deshacer."
    );
});

test("Estado 1: menciona que no se pueden deshacer (irreversibilidad)", () => {
    const texto = construirTextoConfirmacionMulti([{ currency: "ARS" }, { currency: "USD" }]);
    assert.match(texto, /no se pueden deshacer/);
});

// ============================================================================
// etiquetaNotaCredito / estadoVisualNota (Estados 2 y 4 — avance por nota)
// ============================================================================

test("etiquetaNotaCredito: ARS → 'Nota de crédito en $'", () => {
    assert.equal(etiquetaNotaCredito("ARS"), "Nota de crédito en $");
});

test("etiquetaNotaCredito: USD → 'Nota de crédito en US$'", () => {
    assert.equal(etiquetaNotaCredito("USD"), "Nota de crédito en US$");
});

test("estadoVisualNota: Succeeded → ✔ emitida", () => {
    assert.deepEqual(estadoVisualNota("Succeeded"), { icono: "✔", texto: "emitida" });
});

test("estadoVisualNota: Failed → ✗ no salió", () => {
    assert.deepEqual(estadoVisualNota("Failed"), { icono: "✗", texto: "no salió" });
});

test("estadoVisualNota: Pending → ⏳ emitiendo…", () => {
    assert.deepEqual(estadoVisualNota("Pending"), { icono: "⏳", texto: "emitiendo…" });
});

test("estadoVisualNota: estado desconocido → degrada a 'emitiendo…' (nunca rompe)", () => {
    assert.deepEqual(estadoVisualNota("AlgoRaro"), { icono: "⏳", texto: "emitiendo…" });
});

// ============================================================================
// construirTextoExitoMulti (Estado 3 — éxito total)
// ============================================================================

test("Estado 3: copy exacto con 2 notas en $ y US$", () => {
    const creditNotes = [{ currency: "ARS", status: "Succeeded" }, { currency: "USD", status: "Succeeded" }];
    assert.equal(construirTextoExitoMulti(creditNotes), "Se emitieron 2 notas de crédito (una en $ y una en US$).");
});

// ============================================================================
// construirTextoEncabezadoRevision (Estado 4 — falla parcial)
// ============================================================================

test("Estado 4: 1 salió + 1 falló → copy EXACTO de la spec (caso más común)", () => {
    const creditNotes = [{ status: "Succeeded" }, { status: "Failed" }];
    assert.equal(
        construirTextoEncabezadoRevision(creditNotes),
        "La reserva quedó EN REVISIÓN: una nota de crédito salió bien y la otra no. " +
        "La que salió no se deshace."
    );
});

test("Estado 4: 2 salieron + 1 falló (3 facturas) → mensaje generalizado", () => {
    const creditNotes = [{ status: "Succeeded" }, { status: "Succeeded" }, { status: "Failed" }];
    assert.equal(
        construirTextoEncabezadoRevision(creditNotes),
        "La reserva quedó EN REVISIÓN: 2 de 3 notas de crédito salieron bien. Las que salieron no se deshacen."
    );
});

test("Estado 4: menciona que la nota que salió no se deshace", () => {
    const creditNotes = [{ status: "Succeeded" }, { status: "Failed" }];
    assert.match(construirTextoEncabezadoRevision(creditNotes), /no se deshace/);
});

// ============================================================================
// construirTextoFranjaEnRevision (Estado 5 — franja al reabrir la reserva)
// ============================================================================

test("Estado 5: 1 nota faltante → singular 'nota'", () => {
    assert.equal(
        construirTextoFranjaEnRevision(1),
        "En revisión — anulación a medias, falta emitir 1 nota de crédito."
    );
});

test("Estado 5: 2 notas faltantes → plural 'notas'", () => {
    assert.equal(
        construirTextoFranjaEnRevision(2),
        "En revisión — anulación a medias, falta emitir 2 notas de crédito."
    );
});

// ============================================================================
// entradasSaldoAFavor (Estado 3 — línea de saldo a favor, solo si hubo cobros)
// ============================================================================

test("dict con ARS y USD → 2 entradas, nunca sumadas", () => {
    const entradas = entradasSaldoAFavor({ ARS: 150000, USD: 200 });
    assert.equal(entradas.length, 2);
    assert.deepEqual(entradas.find((e) => e.currency === "ARS"), { currency: "ARS", amount: 150000 });
    assert.deepEqual(entradas.find((e) => e.currency === "USD"), { currency: "USD", amount: 200 });
});

test("dict con una moneda en 0 → se filtra (no hubo cobro en esa moneda)", () => {
    const entradas = entradasSaldoAFavor({ ARS: 0, USD: 200 });
    assert.equal(entradas.length, 1);
    assert.equal(entradas[0].currency, "USD");
});

test("dict vacío → sin línea de saldo a favor (Estado 3: no se muestra la línea)", () => {
    assert.deepEqual(entradasSaldoAFavor({}), []);
});

test("null/undefined → array vacío (no rompe)", () => {
    assert.deepEqual(entradasSaldoAFavor(null), []);
    assert.deepEqual(entradasSaldoAFavor(undefined), []);
});
