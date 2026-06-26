/**
 * Constantes de texto (copy) del panel CancelarReservaInline.
 *
 * Centralizar el copy aquí cumple dos funciones:
 *   1. El texto vive en un solo lugar: si hay que cambiarlo, se cambia acá.
 *   2. Los tests importan las mismas constantes que el componente → si alguien
 *      edita un texto en el componente sin pasar por este módulo, el test lo detecta.
 *
 * NUNCA modificar estos textos sin actualizar primero la guía UX (guia-ux-gaston.md
 * sección 2026-06-25) y los tests de cancelarReservaInline.test.mjs.
 */

// ─── Textos de carteles ───────────────────────────────────────────────────────

/**
 * Cartel VERDE — caso DirectCancel (sin factura, sin cobros).
 * Se anula directo sin nota de crédito.
 */
export const TEXTO_BANNER_DIRECT_CANCEL =
    "Esta reserva no tiene factura emitida, se anula directo, sin nota de crédito.";

/**
 * Cartel CELESTE — caso PaymentsToCredit (sin factura, con cobros → saldo a favor).
 * El banner tiene contenido dinámico (los montos por moneda) y "SALDO A FAVOR" en negrita.
 * Se exportan las partes estáticas para que el componente las combine con los montos y el <strong>.
 *
 * Texto completo de la guía:
 * "Esta reserva no tiene factura, pero el cliente ya pagó (MONTOS).
 *  Al anular, esos montos quedan como SALDO A FAVOR del cliente, para usar en otra reserva."
 */
export const TEXTO_BANNER_SALDO_FAVOR_INICIO =
    "Esta reserva no tiene factura, pero el cliente ya pagó";
export const TEXTO_BANNER_SALDO_FAVOR_ANTE_NEGRITA =
    "Al anular, esos montos quedan como ";
export const TEXTO_BANNER_SALDO_FAVOR_NEGRITA =
    "SALDO A FAVOR";
export const TEXTO_BANNER_SALDO_FAVOR_POST_NEGRITA =
    " del cliente, para usar en otra reserva.";

/**
 * Cartel ÁMBAR — caso CreditNote (con factura CAE vivo → emite NC en AFIP/ARCA).
 */
export const TEXTO_BANNER_CREDIT_NOTE =
    "Esta reserva tiene factura emitida, al anular se emite la nota de crédito en AFIP/ARCA para anularla.";

// ─── Mensajes de éxito (textos exactos de guia-ux-gaston.md 2026-06-25) ──────

/**
 * Caso DirectCancel: baja directa, sin nota de crédito.
 * Mensaje corto porque no hay comprobante generado.
 */
export const MENSAJE_EXITO_DIRECT_CANCEL = "Reserva anulada.";

/**
 * Caso PaymentsToCredit: la plata cobrada queda como saldo a favor.
 * El agente necesita saber dónde quedó la plata (regla P4-A, guía 2026-06-25).
 */
export const MENSAJE_EXITO_PAYMENTS_TO_CREDIT =
    "Reserva anulada. Lo cobrado quedó como saldo a favor del cliente.";

/**
 * Caso CreditNote: la nota de crédito se genera en AFIP/ARCA de forma asíncrona.
 * "se está generando" porque el CAE puede demorar unos segundos.
 */
export const MENSAJE_EXITO_CREDIT_NOTE =
    "Reserva anulada. La nota de crédito se está generando.";
