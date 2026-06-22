import { test } from 'node:test';
import assert from 'node:assert/strict';

/**
 * ADR-037 (2026-06-21) — Desacople de facturación (frontend).
 *
 * Réplicas de la lógica de presentación de:
 *  - ReservaStatusChips.jsx (chip de facturación + gating "Debe — no viaja").
 *  - ReservaDetailPage.jsx (ocultar "Emitir factura" cuando ya está facturada del todo).
 *
 * Decisiones de Gaston: chip de facturación SIEMPRE visible; "Debe — no viaja" solo dentro
 * de la ventana de aviso (isWithinUnpaidAlertWindow); en Finalizada ya facturada del todo no
 * se muestra "Emitir factura" (corregir = NC/ND).
 */

// ── Réplica: mapeo del chip de facturación (ReservaStatusChips INVOICING_CHIP) ──
const INVOICING_LABEL = {
    NotInvoiced: 'Sin facturar',
    PartiallyInvoiced: 'Facturada en parte',
    FullyInvoiced: 'Facturada total',
};
function labelChipFactura(invoicingStatus) {
    return (INVOICING_LABEL[invoicingStatus] || INVOICING_LABEL.NotInvoiced);
}

test('chip factura: NotInvoiced → "Sin facturar"', () => {
    assert.equal(labelChipFactura('NotInvoiced'), 'Sin facturar');
});
test('chip factura: PartiallyInvoiced → "Facturada en parte"', () => {
    assert.equal(labelChipFactura('PartiallyInvoiced'), 'Facturada en parte');
});
test('chip factura: FullyInvoiced → "Facturada total"', () => {
    assert.equal(labelChipFactura('FullyInvoiced'), 'Facturada total');
});
test('chip factura: valor ausente/desconocido → "Sin facturar" (fallback)', () => {
    assert.equal(labelChipFactura(undefined), 'Sin facturar');
    assert.equal(labelChipFactura('Otra'), 'Sin facturar');
});

// El chip de facturación se muestra SIEMPRE (en cualquier estado).
function muestraChipFactura(/* reserva */) {
    return true; // ADR-037: siempre visible (incluso pre-venta dirá "Sin facturar").
}
test('chip factura: visible en todos los estados (incluida pre-venta)', () => {
    assert.equal(muestraChipFactura(), true);
});

// ── Réplica: ocultar "Emitir factura" cuando ya está facturada del todo ──
function muestraBotonEmitirFactura(reserva) {
    return reserva.invoicingStatus !== 'FullyInvoiced';
}
test('emitir factura: oculto cuando invoicingStatus === FullyInvoiced (decisión 3A)', () => {
    assert.equal(muestraBotonEmitirFactura({ invoicingStatus: 'FullyInvoiced' }), false);
});
test('emitir factura: visible cuando sin facturar o parcial', () => {
    assert.equal(muestraBotonEmitirFactura({ invoicingStatus: 'NotInvoiced' }), true);
    assert.equal(muestraBotonEmitirFactura({ invoicingStatus: 'PartiallyInvoiced' }), true);
});

// ── Réplica: "Debe — no viaja" solo dentro de la ventana de aviso ──
function muestraDebeNoViaja(reserva) {
    return reserva.status === 'Confirmed'
        && !reserva.isFullyPaid
        && reserva.isWithinUnpaidAlertWindow === true;
}
test('debe-no-viaja: Confirmed + debe + dentro de ventana → se muestra', () => {
    assert.equal(muestraDebeNoViaja({ status: 'Confirmed', isFullyPaid: false, isWithinUnpaidAlertWindow: true }), true);
});
test('debe-no-viaja: Confirmed + debe pero FUERA de ventana → NO se muestra (ADR-037)', () => {
    assert.equal(muestraDebeNoViaja({ status: 'Confirmed', isFullyPaid: false, isWithinUnpaidAlertWindow: false }), false);
});
test('debe-no-viaja: flag ausente → NO se muestra', () => {
    assert.equal(muestraDebeNoViaja({ status: 'Confirmed', isFullyPaid: false }), false);
});
test('debe-no-viaja: pagada → NO se muestra aunque esté en ventana', () => {
    assert.equal(muestraDebeNoViaja({ status: 'Confirmed', isFullyPaid: true, isWithinUnpaidAlertWindow: true }), false);
});
test('debe-no-viaja: otro estado → NO se muestra', () => {
    assert.equal(muestraDebeNoViaja({ status: 'InManagement', isFullyPaid: false, isWithinUnpaidAlertWindow: true }), false);
});
