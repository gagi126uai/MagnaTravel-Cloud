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
// ADR-048 T4 (2026-07-17, spec Punto 2, P1=B FIRMADA): se sumó "FullyReturned" →
// "✓ Facturada y devuelta". A diferencia de los otros tres valores, este NUNCA cae en
// el fallback "Sin facturar" (esa es justo la mentira que la spec corrige).
const INVOICING_LABEL = {
    NotInvoiced: 'Sin facturar',
    PartiallyInvoiced: 'Facturada en parte',
    FullyInvoiced: 'Facturada total',
    FullyReturned: '✓ Facturada y devuelta',
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
test('chip factura: FullyReturned → "✓ Facturada y devuelta" (ADR-048 T4, P1=B FIRMADA)', () => {
    assert.equal(labelChipFactura('FullyReturned'), '✓ Facturada y devuelta');
});
test('chip factura: valor ausente/desconocido → "Sin facturar" (fallback)', () => {
    assert.equal(labelChipFactura(undefined), 'Sin facturar');
    assert.equal(labelChipFactura('Otra'), 'Sin facturar');
});
test('chip factura: FullyReturned NUNCA cae en el fallback "Sin facturar" (la mentira que corrige T4)', () => {
    assert.notEqual(labelChipFactura('FullyReturned'), 'Sin facturar');
});

// ── Réplica: mapeo del chip de facturación en el Estado de Cuenta
//    (EstadoCuentaResumen.jsx ChipInvoicingStatus) — mismo cuarto valor, mismo texto,
//    para que las dos pantallas digan exactamente lo mismo. ANTES de T4 esta función
//    devolvía null (el chip desaparecía) para "FullyReturned": un hueco visual.
function labelChipFacturaEstadoCuenta(status) {
    if (!status || status === 'NotInvoiced') return 'Sin facturar';
    if (status === 'PartiallyInvoiced') return 'Facturada en parte';
    if (status === 'FullyInvoiced') return 'Facturada total';
    if (status === 'FullyReturned') return '✓ Facturada y devuelta';
    return null;
}
test('chip factura (Estado de Cuenta): FullyReturned → "✓ Facturada y devuelta", ya NO desaparece', () => {
    assert.equal(labelChipFacturaEstadoCuenta('FullyReturned'), '✓ Facturada y devuelta');
});
test('chip factura (Estado de Cuenta): los tres valores previos siguen iguales', () => {
    assert.equal(labelChipFacturaEstadoCuenta(null), 'Sin facturar');
    assert.equal(labelChipFacturaEstadoCuenta('PartiallyInvoiced'), 'Facturada en parte');
    assert.equal(labelChipFacturaEstadoCuenta('FullyInvoiced'), 'Facturada total');
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
