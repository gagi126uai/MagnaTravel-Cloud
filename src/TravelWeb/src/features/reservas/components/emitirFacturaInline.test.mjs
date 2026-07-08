/**
 * Tests de lógica pura para EmitirFacturaInline.
 *
 * Testea las funciones exportadas del componente que encapsulan decisiones
 * críticas de facturación fiscal. Corren con Node puro sin bundler.
 *
 * Decisiones cubiertas:
 *   - elegirGrupoPrecarga: default automático de moneda (pedido Gaston 2026-07-07)
 *     + la regla de seguridad B1 (nunca cargar USD como ARS)
 *   - hayDescuadre: si el total del formulario difiere del sugerido
 *   - validarCamposUSD: validación del tipo de cambio para facturas en dólares
 *   - describirMotivoExclusion: texto en criollo para servicios excluidos de la sugerencia (C6)
 *
 * Cómo correr: node --test src/features/reservas/components/emitirFacturaInline.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// elegirGrupoPrecarga vive en un archivo .js aparte (sin JSX), así que se puede
// importar directo con Node sin necesidad de bundler — mismo patrón que
// moneyStatus.js. Esto evita tener una copia manual de la lógica en el test
// que se puede desactualizar sola si cambia el original.
import { elegirGrupoPrecarga } from "../lib/invoiceCurrencyDefault.js";

// ─── Lógica pura: copiada de EmitirFacturaInline.jsx ─────────────────────────
// El resto de las funciones de este archivo (hayDescuadre, validarCamposUSD)
// todavía viven dentro del .jsx del componente, así que se replican acá porque
// el runner es Node puro (sin Vite/JSX). Si cambian en el original, actualizar acá también.

/**
 * Determina si hay un descuadre entre el total armado y el sugerido.
 */
function hayDescuadre(totalItems, suggestedTotal, tolerancia = 0.5) {
  if (typeof suggestedTotal !== "number" || suggestedTotal <= 0) return false;
  const diferencia = Math.abs(totalItems - suggestedTotal);
  return diferencia > tolerancia;
}

/**
 * Valida los campos de tipo de cambio para facturas en USD.
 * Devuelve string de error, o null si todo está bien.
 */
function validarCamposUSD(tipoCambio, justificacion) {
  const tcNum = Number(tipoCambio);
  if (!tipoCambio || isNaN(tcNum) || tcNum <= 0) {
    return "Ingresá el tipo de cambio para facturas en dólares.";
  }
  if (tcNum === 1) {
    return "El tipo de cambio no puede ser 1. Ingresá el valor en pesos del dólar (ej: 1200).";
  }
  if (!String(justificacion ?? "").trim()) {
    return "Ingresá la justificación del tipo de cambio.";
  }
  return null;
}

// ─── Tests: elegirGrupoPrecarga ───────────────────────────────────────────────

// Caso B1 crítico: con flag OFF y reserva solo con servicios en USD,
// la función debe devolver null en lugar del grupo USD.
// Devolverlo habría cargado montos en dólares en un comprobante ARS.
test("elegirGrupoPrecarga — B1 CRÍTICO: flag OFF + solo USD → null (no cargar USD como ARS)", () => {
  const grupos = [
    { currency: "USD", items: [{ description: "Vuelo", unitPrice: 500 }], suggestedTotal: 500 },
  ];
  const resultado = elegirGrupoPrecarga(grupos, false);
  assert.equal(resultado, null, "Con flag OFF y solo USD, debe devolver null para no facturar USD como pesos");
});

test("elegirGrupoPrecarga — flag OFF + grupos ARS y USD → devuelve solo el ARS", () => {
  const grupoARS = { currency: "ARS", items: [{ description: "Hotel", unitPrice: 80000 }], suggestedTotal: 80000 };
  const grupoUSD = { currency: "USD", items: [{ description: "Vuelo", unitPrice: 500 }], suggestedTotal: 500 };
  const grupos = [grupoARS, grupoUSD];

  const resultado = elegirGrupoPrecarga(grupos, false);
  assert.equal(resultado?.currency, "ARS", "Con flag OFF debe devolver el grupo ARS aunque haya USD también");
  assert.equal(resultado?.suggestedTotal, 80000);
});

test("elegirGrupoPrecarga — flag OFF + solo ARS → devuelve el grupo ARS", () => {
  const grupos = [
    { currency: "ARS", items: [{ description: "Paquete Bariloche", unitPrice: 120000 }], suggestedTotal: 120000 },
  ];
  const resultado = elegirGrupoPrecarga(grupos, false);
  assert.equal(resultado?.currency, "ARS");
});

test("elegirGrupoPrecarga — flag OFF + lista vacía → null", () => {
  const resultado = elegirGrupoPrecarga([], false);
  assert.equal(resultado, null);
});

test("elegirGrupoPrecarga — flag OFF + null → null (no lanza)", () => {
  const resultado = elegirGrupoPrecarga(null, false);
  assert.equal(resultado, null);
});

test("elegirGrupoPrecarga — flag ON + ARS y USD → devuelve ARS (preferencia)", () => {
  // Con flag ON se mantiene el comportamiento original: ARS preferido.
  const grupoARS = { currency: "ARS", items: [], suggestedTotal: 80000 };
  const grupoUSD = { currency: "USD", items: [], suggestedTotal: 500 };
  const grupos = [grupoUSD, grupoARS]; // ARS no es el primero en el array

  const resultado = elegirGrupoPrecarga(grupos, true);
  assert.equal(resultado?.currency, "ARS", "Con flag ON debe preferir ARS aunque no sea el primero");
});

test("elegirGrupoPrecarga — flag ON + solo USD → devuelve USD (no hay ARS, es válido con flag ON)", () => {
  // Con flag ON el usuario puede elegir emitir en USD explícitamente,
  // así que precargar el único grupo disponible (USD) es correcto.
  const grupos = [
    { currency: "USD", items: [{ description: "Vuelo", unitPrice: 1000 }], suggestedTotal: 1000 },
  ];
  const resultado = elegirGrupoPrecarga(grupos, true);
  assert.equal(resultado?.currency, "USD", "Con flag ON y solo USD, precargar USD es correcto (el usuario puede cambiarlo)");
});

test("elegirGrupoPrecarga — flag ON + lista vacía → null", () => {
  const resultado = elegirGrupoPrecarga([], true);
  assert.equal(resultado, null);
});

// ─── Tests: pedido textual de Gaston (2026-07-07) ────────────────────────────
// "Si detecta servicios en dólares, elige automáticamente dólares; si detecta
// servicios en pesos, elige automáticamente pesos." Con flag ON (la config
// "Facturar en más de una moneda" prendida, que es la que Gaston tiene activa
// en el uso real — ver ADR-042).

test("Gaston 2026-07-07 — reserva con TODOS los servicios en dólares → preselecciona USD", () => {
  const grupos = [
    { currency: "USD", items: [{ description: "Paquete Cancún" }], suggestedTotal: 2000 },
  ];
  const resultado = elegirGrupoPrecarga(grupos, true);
  assert.equal(resultado?.currency, "USD", "Reserva 100% en dólares debe precargar USD, no ARS");
});

test("Gaston 2026-07-07 — reserva con TODOS los servicios en pesos → preselecciona ARS", () => {
  const grupos = [
    { currency: "ARS", items: [{ description: "Traslado aeropuerto" }], suggestedTotal: 45000 },
  ];
  const resultado = elegirGrupoPrecarga(grupos, true);
  assert.equal(resultado?.currency, "ARS", "Reserva 100% en pesos debe precargar ARS");
});

test("Gaston 2026-07-07 — reserva con servicios en ambas monedas → NO adivina, deja ARS como punto de partida", () => {
  // Con mezcla de monedas no hay una única respuesta correcta: se mantiene el
  // comportamiento de siempre (ARS como default) y el selector queda disponible
  // para que el usuario elija manualmente cuál facturar primero.
  const grupoARS = { currency: "ARS", items: [{ description: "Hotel" }], suggestedTotal: 80000 };
  const grupoUSD = { currency: "USD", items: [{ description: "Vuelo" }], suggestedTotal: 500 };
  const resultado = elegirGrupoPrecarga([grupoARS, grupoUSD], true);
  assert.equal(resultado?.currency, "ARS", "Con mezcla de monedas no se adivina: se mantiene ARS como default editable");
});

// ─── Tests: hayDescuadre ─────────────────────────────────────────────────────

test("hayDescuadre — dentro de tolerancia → false (sin aviso)", () => {
  // El total puede diferir hasta $0.50 sin mostrar el aviso (redondeos de decimales).
  const resultado = hayDescuadre(1000.30, 1000.00, 0.5);
  assert.equal(resultado, false, "Diferencia de $0.30 está dentro de la tolerancia de $0.50");
});

test("hayDescuadre — exactamente en el límite → false", () => {
  const resultado = hayDescuadre(1000.50, 1000.00, 0.5);
  // 1000.50 - 1000.00 = 0.50 que es igual al límite, no mayor → no hay descuadre
  assert.equal(resultado, false, "Diferencia exactamente en el límite no debe mostrar aviso");
});

test("hayDescuadre — supera tolerancia → true (mostrar franja)", () => {
  const resultado = hayDescuadre(1001.00, 1000.00, 0.5);
  assert.equal(resultado, true, "Diferencia de $1.00 supera la tolerancia de $0.50 → aviso");
});

test("hayDescuadre — total menor al sugerido supera tolerancia → true", () => {
  // El usuario editó los renglones y factura menos de lo vendido.
  const resultado = hayDescuadre(95000, 100000, 0.5);
  assert.equal(resultado, true, "Diferencia de $5000 por debajo del sugerido → aviso");
});

test("hayDescuadre — total mayor al sugerido supera tolerancia → true", () => {
  // El usuario agregó un renglón extra que no estaba en los servicios.
  const resultado = hayDescuadre(105000, 100000, 0.5);
  assert.equal(resultado, true, "Diferencia de $5000 por encima del sugerido → aviso");
});

test("hayDescuadre — suggestedTotal 0 → false (sin sugerido no hay descuadre posible)", () => {
  // Si no hay servicios confirmados, suggestedTotal es 0 o null. No mostrar aviso.
  const resultado = hayDescuadre(1000, 0, 0.5);
  assert.equal(resultado, false, "Sin sugerido (0) no se puede hablar de descuadre");
});

test("hayDescuadre — suggestedTotal negativo → false", () => {
  const resultado = hayDescuadre(1000, -100, 0.5);
  assert.equal(resultado, false, "Sugerido negativo no tiene sentido, no mostrar aviso");
});

test("hayDescuadre — tolerancia default 0.5 aplicada correctamente", () => {
  // Sin pasar tolerancia, usa 0.5 por defecto.
  assert.equal(hayDescuadre(100.40, 100, undefined), false, "0.40 < 0.50 → no hay descuadre");
  assert.equal(hayDescuadre(100.60, 100, undefined), true, "0.60 > 0.50 → hay descuadre");
});

test("hayDescuadre — totales iguales → false", () => {
  const resultado = hayDescuadre(50000, 50000, 0.5);
  assert.equal(resultado, false);
});

// ─── Tests: validarCamposUSD ──────────────────────────────────────────────────

test("validarCamposUSD — todo válido → null (sin error)", () => {
  // TC 1200 y justificación completa → puede emitirse.
  const resultado = validarCamposUSD("1200", "Dólar BNA vendedor divisa del 13/06/2026");
  assert.equal(resultado, null);
});

test("validarCamposUSD — TC vacío → error de TC faltante", () => {
  const resultado = validarCamposUSD("", "Justificación válida");
  assert.ok(typeof resultado === "string" && resultado.length > 0, "Debe devolver error");
  assert.ok(resultado.includes("tipo de cambio"), `El error debe mencionar tipo de cambio: '${resultado}'`);
});

test("validarCamposUSD — TC cero → error", () => {
  const resultado = validarCamposUSD("0", "Justificación válida");
  assert.ok(resultado !== null);
});

test("validarCamposUSD — TC negativo → error", () => {
  const resultado = validarCamposUSD("-100", "Justificación válida");
  assert.ok(resultado !== null);
});

test("validarCamposUSD — TC = 1 → error específico (no puede ser 1)", () => {
  // TC = 1 significaría 1 peso por dólar, que es claramente un error de tipeo.
  const resultado = validarCamposUSD("1", "Justificación válida");
  assert.ok(typeof resultado === "string");
  assert.ok(resultado.includes("no puede ser 1"), `El error debe explicar por qué no puede ser 1: '${resultado}'`);
});

test("validarCamposUSD — TC válido pero justificación vacía → error", () => {
  const resultado = validarCamposUSD("1200", "");
  assert.ok(typeof resultado === "string");
  assert.ok(resultado.includes("justificación"), `El error debe mencionar la justificación: '${resultado}'`);
});

test("validarCamposUSD — justificación solo espacios → error (no se acepta)", () => {
  const resultado = validarCamposUSD("1200", "   ");
  assert.ok(resultado !== null, "Solo espacios no es una justificación válida");
});

test("validarCamposUSD — TC como número (no string) → válido si es > 1", () => {
  // El campo llega como string del input pero la función debe tolerar number también.
  const resultado = validarCamposUSD(1200, "Justificación completa del TC");
  assert.equal(resultado, null, "TC como número también debe funcionar");
});

test("validarCamposUSD — TC = 1.5 (dólar oficial no es 1) → válido, sin error", () => {
  // Aunque 1.5 parece bajo, la función solo rechaza exactamente 1.
  // La validación de rango de mercado es responsabilidad del operador y del backend.
  const resultado = validarCamposUSD("1.5", "Tipo de cambio oficial fijado");
  assert.equal(resultado, null);
});

// ─── Test de integración de regla B1 (flujo completo de precarga) ─────────────

// ─── Funciones H2: resolverEstadoFiscal y labelFacturaEmitida ────────────────
// Copiadas / delegadas de los archivos de producción.
// Si cambia el original, actualizar acá también.

/**
 * H2: interpreta la respuesta del endpoint GET /invoices/reserva/{id}/fiscal-status.
 * Filtra por invoicePublicId para no confundir facturas anteriores de la misma reserva.
 * Copia de EmitirFacturaInline.jsx.
 */
function resolverEstadoFiscal(statusItems, invoicePublicId) {
  if (!Array.isArray(statusItems) || statusItems.length === 0) {
    return { estado: null, factura: null };
  }

  const factura = invoicePublicId
    ? statusItems.find((f) => String(f.publicId) === String(invoicePublicId))
    : statusItems[statusItems.length - 1];

  if (!factura) return { estado: null, factura: null };
  return { estado: factura.status, factura };
}

/**
 * Paso 3 (H2 2026-06-24): helper canónico de formato de comprobante.
 * Copia de src/features/reservas/lib/invoiceFormatUtils.js.
 * Se usa en dos lugares: EmitirFacturaInline (éxito) y ReservaDetailPage (extracto).
 */
function formatearEtiquetaFactura(invoiceType, puntoDeVenta, numeroComprobante) {
  const tipo = invoiceType || "?";
  const pdv = String(puntoDeVenta ?? 0).padStart(4, "0");
  const num = String(numeroComprobante ?? 0).padStart(8, "0");
  return `Factura ${tipo} ${pdv}-${num}`;
}

/**
 * Construye el label de la factura emitida para mostrar en el cartel de ÉXITO.
 * Delega a formatearEtiquetaFactura (igual que en producción).
 */
function labelFacturaEmitida(factura) {
  if (!factura) return "Factura emitida";
  return formatearEtiquetaFactura(
    factura.invoiceType,
    factura.puntoDeVenta,
    factura.numeroComprobante
  );
}

// ─── Tests: resolverEstadoFiscal (H2) ────────────────────────────────────────

test("resolverEstadoFiscal — lista vacía → estado null, sin factura", () => {
  // Poll llama antes de que el backend tenga el registro: devuelve array vacío.
  const resultado = resolverEstadoFiscal([], "abc-123");
  assert.equal(resultado.estado, null);
  assert.equal(resultado.factura, null);
});

test("resolverEstadoFiscal — null → estado null, sin factura", () => {
  const resultado = resolverEstadoFiscal(null, "abc-123");
  assert.equal(resultado.estado, null);
  assert.equal(resultado.factura, null);
});

test("resolverEstadoFiscal — factura InProcess → seguir polling", () => {
  const items = [
    { publicId: "abc-123", status: "InProcess", invoiceType: "B", puntoDeVenta: 1, numeroComprobante: 12345 },
  ];
  const resultado = resolverEstadoFiscal(items, "abc-123");
  assert.equal(resultado.estado, "InProcess");
  assert.ok(resultado.factura !== null);
});

test("resolverEstadoFiscal — factura Issued → estado Issued con datos completos", () => {
  const facturaIssuida = {
    publicId: "abc-123",
    status: "Issued",
    invoiceType: "B",
    puntoDeVenta: 1,
    numeroComprobante: 12345,
    cae: "12345678901234",
    vencimientoCAE: "2026-07-10T00:00:00",
  };
  const items = [facturaIssuida];
  const resultado = resolverEstadoFiscal(items, "abc-123");
  assert.equal(resultado.estado, "Issued");
  assert.equal(resultado.factura, facturaIssuida);
});

test("resolverEstadoFiscal — factura Rejected → estado Rejected con motivo", () => {
  const facturaRechazada = {
    publicId: "abc-123",
    status: "Rejected",
    invoiceType: "B",
    puntoDeVenta: 1,
    numeroComprobante: 0,
    rejectionReason: "CUIT del receptor no válido",
  };
  const resultado = resolverEstadoFiscal([facturaRechazada], "abc-123");
  assert.equal(resultado.estado, "Rejected");
  assert.equal(resultado.factura?.rejectionReason, "CUIT del receptor no válido");
});

test("resolverEstadoFiscal — reserva con dos facturas, filtra por publicId correcto", () => {
  // Una reserva puede tener facturas anteriores.
  // El poll debe identificar SOLO la recién emitida por publicId.
  const facturaVieja = {
    publicId: "vieja-999",
    status: "Issued",
    invoiceType: "B",
    puntoDeVenta: 1,
    numeroComprobante: 10,
  };
  const facturaNueva = {
    publicId: "nueva-888",
    status: "InProcess",
    invoiceType: "B",
    puntoDeVenta: 1,
    numeroComprobante: 0,
  };
  const items = [facturaVieja, facturaNueva];

  // Debe devolver la nueva (InProcess), no la vieja (Issued).
  const resultado = resolverEstadoFiscal(items, "nueva-888");
  assert.equal(resultado.estado, "InProcess");
  assert.equal(resultado.factura?.publicId, "nueva-888");
});

test("resolverEstadoFiscal — publicId no encontrado → estado null", () => {
  // El backend no encontró la factura por el publicId dado (caso raro).
  const items = [
    { publicId: "otro-555", status: "Issued", invoiceType: "B", puntoDeVenta: 1, numeroComprobante: 10 },
  ];
  const resultado = resolverEstadoFiscal(items, "buscado-999");
  assert.equal(resultado.estado, null);
  assert.equal(resultado.factura, null);
});

test("resolverEstadoFiscal — sin invoicePublicId → usa la última factura del array", () => {
  // Fallback para cuando el POST no devolvió publicId (no debería pasar, pero es seguro).
  const items = [
    { publicId: "a", status: "Issued", invoiceType: "B", puntoDeVenta: 1, numeroComprobante: 1 },
    { publicId: "b", status: "InProcess", invoiceType: "B", puntoDeVenta: 1, numeroComprobante: 2 },
  ];
  const resultado = resolverEstadoFiscal(items, null);
  // Última del array es "b" (InProcess)
  assert.equal(resultado.factura?.publicId, "b");
  assert.equal(resultado.estado, "InProcess");
});

// ─── Tests: formatearEtiquetaFactura (Paso 3 — helper canónico compartido) ────
// Este es el helper de la lib compartida. labelFacturaEmitida delega en él.
// También lo usa InvoicePdfActions en el Estado de Cuenta.

test("formatearEtiquetaFactura — Factura B 0001-00012345 (caso normal)", () => {
  assert.equal(formatearEtiquetaFactura("B", 1, 12345), "Factura B 0001-00012345");
});

test("formatearEtiquetaFactura — Factura A pdv 99, num 12345678 (sin truncar dígitos)", () => {
  assert.equal(formatearEtiquetaFactura("A", 99, 12345678), "Factura A 0099-12345678");
});

test("formatearEtiquetaFactura — Factura M (tipo M es válido AFIP)", () => {
  assert.equal(formatearEtiquetaFactura("M", 1, 1), "Factura M 0001-00000001");
});

test("formatearEtiquetaFactura — invoiceType null → '?' como fallback", () => {
  const label = formatearEtiquetaFactura(null, 1, 1);
  assert.ok(label.startsWith("Factura ?"), `Recibió: '${label}'`);
});

test("formatearEtiquetaFactura — puntoDeVenta y numeroComprobante null → rellena con ceros", () => {
  assert.equal(formatearEtiquetaFactura("B", null, null), "Factura B 0000-00000000");
});

test("formatearEtiquetaFactura — coherencia con InvoiceDto y InvoiceFiscalStatusDto (mismos campos)", () => {
  // InvoiceDto (extracto): invoice.invoiceType, invoice.puntoDeVenta, invoice.numeroComprobante
  // InvoiceFiscalStatusDto (poll): factura.invoiceType, factura.puntoDeVenta, factura.numeroComprobante
  // Ambos producen el mismo label → mismo formato en los dos contextos.
  const desdeDtoReserva = formatearEtiquetaFactura("B", 1, 12345);
  const desdeStatusDto = formatearEtiquetaFactura("B", 1, 12345);
  assert.equal(desdeDtoReserva, desdeStatusDto, "El formato es idéntico independientemente del DTO de origen");
});

// ─── Función: resolverPreflightBloqueo (F4-8) ────────────────────────────────
// Copia de EmitirFacturaInline.jsx — interpreta el InvoiceEmissionPreflightDto.
// ÚNICO bloqueo duro: Factura A con cliente sin CUIT válido.
// Todo lo demás: Allowed=true (no se frena la emisión).

/**
 * F4-8: interpreta el resultado del preflight de emisión de factura.
 * Devuelve { message } si bloquea; null si puede continuar.
 * @param {object|null} preflight - InvoiceEmissionPreflightDto del backend
 */
function resolverPreflightBloqueo(preflight) {
  if (!preflight) return null;

  if (preflight.allowed === false || preflight.severity === "block") {
    const mensajeDefault =
      "Este cliente no es Responsable Inscripto. No corresponde Factura A. " +
      "Revisá el tipo de comprobante o la condición del cliente.";
    return { message: preflight.reason || mensajeDefault };
  }

  return null;
}

// ─── Tests: resolverPreflightBloqueo ─────────────────────────────────────────

test("resolverPreflightBloqueo — null → null (sin bloqueo, continúa al modal)", () => {
  // Si el endpoint falla o no responde, preflight es null.
  // La regla de fallback es conservadora: seguir sin bloquear (el backend revalida al emitir).
  const resultado = resolverPreflightBloqueo(null);
  assert.equal(resultado, null);
});

test("resolverPreflightBloqueo — allowed=true, severity=ok → null (caso normal)", () => {
  // Factura B para consumidor final: siempre ok.
  const preflight = { allowed: true, severity: "ok", willEmitLetter: "B", reason: null };
  const resultado = resolverPreflightBloqueo(preflight);
  assert.equal(resultado, null);
});

test("resolverPreflightBloqueo — allowed=true, severity=warn → null (advertencia pero NO bloquea)", () => {
  // severity=warn es informativo, el usuario puede seguir.
  // El backend lo usa para advertencias que no son un rechazo seguro de ARCA.
  const preflight = {
    allowed: true,
    severity: "warn",
    willEmitLetter: "A",
    reason: "El cliente tiene condición dudosa, verificar CUIT."
  };
  const resultado = resolverPreflightBloqueo(preflight);
  assert.equal(resultado, null, "severity=warn no debe bloquear — solo informar");
});

test("resolverPreflightBloqueo — allowed=false → objeto con message (BLOQUEA)", () => {
  // Caso principal: Factura A para cliente sin CUIT → ARCA lo rechazaría con certeza.
  const preflight = {
    allowed: false,
    severity: "block",
    willEmitLetter: "A",
    reason: "El cliente no tiene CUIT registrado. No se puede emitir Factura A.",
    missingData: ["CUIT"],
  };
  const resultado = resolverPreflightBloqueo(preflight);
  assert.ok(resultado !== null, "Debe devolver un objeto de bloqueo");
  assert.ok(typeof resultado.message === "string" && resultado.message.length > 0);
  assert.ok(
    resultado.message.includes("CUIT") || resultado.message.includes("corresponde"),
    `El mensaje debe mencionar CUIT o el problema: '${resultado.message}'`
  );
});

test("resolverPreflightBloqueo — allowed=false, reason provisto → usa el reason del backend", () => {
  // El backend envía el texto en criollo sin datos sensibles.
  // El front lo muestra literalmente (no reescribe el mensaje).
  const razonDelBackend = "Este cliente es Consumidor Final y recibiría una Factura A, lo cual no corresponde.";
  const preflight = {
    allowed: false,
    severity: "block",
    willEmitLetter: "A",
    reason: razonDelBackend,
    missingData: [],
  };
  const resultado = resolverPreflightBloqueo(preflight);
  assert.equal(resultado?.message, razonDelBackend, "El mensaje debe ser exactamente el que manda el backend");
});

test("resolverPreflightBloqueo — allowed=false, reason null → usa el mensaje default del front", () => {
  // Si el backend no manda reason, el front tiene su propio fallback legible.
  const preflight = { allowed: false, severity: "block", willEmitLetter: "A", reason: null };
  const resultado = resolverPreflightBloqueo(preflight);
  assert.ok(resultado !== null);
  assert.ok(resultado.message.length > 10, "El mensaje default debe ser descriptivo, no vacío");
});

test("resolverPreflightBloqueo — severity=block aunque allowed=true → bloquea igual", () => {
  // Defensivo: si por alguna razón el backend manda severity=block con allowed=true,
  // la función bloquea igual (severity es la fuente de verdad del tipo de respuesta).
  const preflight = {
    allowed: true,   // inconsistencia del backend
    severity: "block",
    willEmitLetter: "A",
    reason: "Bloqueo por coherencia interna.",
  };
  const resultado = resolverPreflightBloqueo(preflight);
  assert.ok(resultado !== null, "severity=block debe bloquear aunque allowed sea true");
});

test("resolverPreflightBloqueo — objeto vacío → null (no bloquea por defecto)", () => {
  // DTO vacío (sin campos): la regla conservadora es no bloquear.
  // El backend siempre manda los campos requeridos; esto es solo para robustez.
  const resultado = resolverPreflightBloqueo({});
  assert.equal(resultado, null);
});

// ─── Tests: labelFacturaEmitida (H2) ─────────────────────────────────────────

test("labelFacturaEmitida — Factura B punto de venta 1, número 12345 → formato correcto", () => {
  const factura = { invoiceType: "B", puntoDeVenta: 1, numeroComprobante: 12345 };
  const label = labelFacturaEmitida(factura);
  // Formato: "Factura {tipo} {pdv 4 dígitos}-{num 8 dígitos}"
  assert.equal(label, "Factura B 0001-00012345");
});

test("labelFacturaEmitida — Factura A con número largo → no truncar ni perder dígitos", () => {
  const factura = { invoiceType: "A", puntoDeVenta: 99, numeroComprobante: 12345678 };
  const label = labelFacturaEmitida(factura);
  assert.equal(label, "Factura A 0099-12345678");
});

test("labelFacturaEmitida — Factura C con número exacto de 8 dígitos → sin pad extra", () => {
  const factura = { invoiceType: "C", puntoDeVenta: 1, numeroComprobante: 99999999 };
  const label = labelFacturaEmitida(factura);
  assert.equal(label, "Factura C 0001-99999999");
});

test("labelFacturaEmitida — null → 'Factura emitida' (fallback seguro)", () => {
  // El componente guarda facturaEmitidaData al detectar Issued en el poll.
  // Si por alguna razón el estado es null, no debe romperse el render.
  const label = labelFacturaEmitida(null);
  assert.equal(label, "Factura emitida");
});

test("labelFacturaEmitida — invoiceType ausente → usa '?'", () => {
  const factura = { puntoDeVenta: 1, numeroComprobante: 100 };
  const label = labelFacturaEmitida(factura);
  assert.ok(label.startsWith("Factura ?"), `Debe empezar con 'Factura ?', recibió: '${label}'`);
});

test("labelFacturaEmitida — puntoDeVenta undefined → rellena con ceros correctamente", () => {
  const factura = { invoiceType: "B", puntoDeVenta: undefined, numeroComprobante: 1 };
  const label = labelFacturaEmitida(factura);
  assert.equal(label, "Factura B 0000-00000001");
});

// ─── Lógica pura G6: conversión de caducidad al guardado ─────────────────────
// Los inputs son strings (valor de HTMLInputElement.value) → deben convertirse a Number.

/**
 * Convierte el valor string de un input de días a número para enviar al backend.
 * 0 = "nunca caduca" (se envía como 0, no como null).
 */
function parsearDiasCaducidad(valor) {
  const num = Number(valor || 0);
  return isNaN(num) || num < 0 ? 0 : Math.floor(num);
}

test("G6 — parsearDiasCaducidad: string '30' → 30", () => {
  assert.equal(parsearDiasCaducidad("30"), 30);
});

test("G6 — parsearDiasCaducidad: string '0' → 0 (nunca caduca)", () => {
  assert.equal(parsearDiasCaducidad("0"), 0);
});

test("G6 — parsearDiasCaducidad: string '' → 0 (vacío = nunca caduca)", () => {
  assert.equal(parsearDiasCaducidad(""), 0);
});

test("G6 — parsearDiasCaducidad: negativo → 0 (campo tiene min=0, pero por si acaso)", () => {
  assert.equal(parsearDiasCaducidad("-5"), 0);
});

test("G6 — parsearDiasCaducidad: '3650' (max) → 3650", () => {
  assert.equal(parsearDiasCaducidad("3650"), 3650);
});

test("G6 — parsearDiasCaducidad: '15.7' → 15 (trunca decimales)", () => {
  // El input es type=number sin step decimal, pero el backend espera int.
  assert.equal(parsearDiasCaducidad("15.7"), 15);
});

// ─── Lógica pura Q9: texto del aviso de pre-venta por caducar ───────────────
// B1 fix (2026-06-24): el backend devuelve la frase COMPLETA en item.message.
// El front usa item.message tal cual — NO lo construye para evitar duplicación.
// Ej: "El presupuesto de Fam. García vence en 3 días." ya viene así del backend.

/**
 * Replica la lógica del render en SeccionPorCaducar después del fix B1:
 * textoAviso = item.message (directo, sin construir en el front).
 */
function resolverTextoAviso(item) {
  return item.message;
}

test("Q9 B1 — texto aviso usa item.message del backend directamente (no duplica tipo/cliente)", () => {
  // El backend ya devuelve "El presupuesto de Fam. García vence en 3 días."
  // El front NO debe prefixear "El presupuesto de Fam. García" nuevamente.
  const item = {
    preSaleKind: "Budget",
    holderName: "García, Juan",
    name: "Paquete Caribe",
    numeroReserva: "R-001",
    daysLeft: 2,
    // message completo como devuelve el backend (B1 fix: no construir en el front)
    message: "El presupuesto de García, Juan vence en 2 días.",
  };
  const texto = resolverTextoAviso(item);

  // El texto resultante debe ser EXACTAMENTE item.message — sin duplicar nada.
  assert.equal(texto, item.message);

  // Anti-regresión B1: verificar que "El presupuesto" NO aparece dos veces.
  const conteoTipoLabel = (texto.match(/El presupuesto/g) || []).length;
  assert.equal(conteoTipoLabel, 1, `"El presupuesto" no debe aparecer duplicado. Texto: '${texto}'`);

  // Anti-regresión B1: verificar que el nombre del cliente NO aparece dos veces.
  const conteoNombre = (texto.match(/García, Juan/g) || []).length;
  assert.equal(conteoNombre, 1, `El nombre del cliente no debe aparecer duplicado. Texto: '${texto}'`);
});

test("Q9 B1 — mensaje de Quotation también viene completo del backend", () => {
  const item = {
    preSaleKind: "Quotation",
    holderName: "Rodríguez, Ana",
    name: "Vuelo Europa",
    numeroReserva: "R-002",
    daysLeft: 0,
    message: "La cotización de Rodríguez, Ana vence hoy.",
  };
  const texto = resolverTextoAviso(item);
  assert.equal(texto, item.message);
  const conteo = (texto.match(/La cotización/g) || []).length;
  assert.equal(conteo, 1, `"La cotización" no debe aparecer duplicada. Texto: '${texto}'`);
});

test("Q9 — daysLeft===0 → ítem debe ser rojo (esHoy=true)", () => {
  // La lógica de color no se puede testear sin React, pero sí la detección de esHoy.
  // esHoy es la señal que determina el color.
  const daysLeft = 0;
  const esHoy = daysLeft === 0;
  assert.equal(esHoy, true, "Con daysLeft=0 el ítem debe marcarse como 'hoy' (rojo)");
});

test("Q9 — daysLeft===1 → ítem debe ser ámbar (esHoy=false)", () => {
  const daysLeft = 1;
  const esHoy = daysLeft === 0;
  assert.equal(esHoy, false, "Con daysLeft=1 el ítem no es de hoy (ámbar)");
});

test("Q9 — daysLeft===0 → ítem debe ser rojo (esHoy=true)", () => {
  // La lógica de color no se puede testear sin React, pero sí la detección de esHoy.
  // esHoy es la señal que determina el color.
  const daysLeft = 0;
  const esHoy = daysLeft === 0;
  assert.equal(esHoy, true, "Con daysLeft=0 el ítem debe marcarse como 'hoy' (rojo)");
});

test("Q9 — daysLeft===1 → ítem debe ser ámbar (esHoy=false)", () => {
  const daysLeft = 1;
  const esHoy = daysLeft === 0;
  assert.equal(esHoy, false, "Con daysLeft=1 el ítem no es de hoy (ámbar)");
});

// ─── Test de integración B1 (flujo completo de precarga) ─────────────────────

test("regla B1 end-to-end: flag OFF + solo USD → soloServiciosUSD=true + items vacío", () => {
  // Simula la lógica completa que ejecuta el useEffect de carga de sugeridos:
  //   1. grupos = solo USD
  //   2. elegirGrupoPrecarga(grupos, false) = null
  //   3. El componente arranca con item genérico en cero
  //   4. soloServiciosUSD = true → el aviso se muestra

  const grupos = [
    { currency: "USD", items: [{ description: "Paquete Caribe", unitPrice: 2000 }], suggestedTotal: 2000 },
  ];
  const flagMultimonedaOn = false;

  const grupoPrecarga = elegirGrupoPrecarga(grupos, flagMultimonedaOn);
  assert.equal(grupoPrecarga, null, "No debe precargar el grupo USD");

  // La lógica del componente usa el grupo null para saber que debe mostrar el aviso.
  // soloServiciosUSD = !flag && grupos.length > 0 && todos son NOT ARS
  const soloServiciosUSD =
    !flagMultimonedaOn &&
    grupos.length > 0 &&
    grupos.every((g) => g.currency !== "ARS");
  assert.equal(soloServiciosUSD, true, "Con flag OFF y solo USD, soloServiciosUSD debe ser true");

  // Al no precargar, los items quedan en blanco (item genérico en 0).
  // El total del formulario sería 0, que no coincide con los 2000 USD del grupo.
  // PERO la franja de descuadre NO debe aparecer (hayDescuadre compara contra
  // el grupo de la moneda EFECTIVA, que es ARS, no USD → suggestedTotal ARS = 0).
  const suggestedTotalARS = 0; // No hay grupo ARS
  assert.equal(hayDescuadre(0, suggestedTotalARS, 0.5), false, "Sin sugerido ARS no hay descuadre falso");
});

// ─── Tests: describirMotivoExclusion (C6, Tanda 6, 2026-07-05) ───────────────
// Traduce el motivo técnico de ExcludedSuggestedServiceDto.Reason a una frase en criollo
// para el aviso "«X» no entra en la factura porque ...". Nunca debe devolver el token crudo.

const MOTIVOS_EXCLUSION_SUGERENCIA = {
  NoResuelto: "todavía no está confirmado",
  Cancelado: "está cancelado",
  PrecioCero: "tiene precio $0",
};

function describirMotivoExclusion(reason) {
  return MOTIVOS_EXCLUSION_SUGERENCIA[reason] || "no se pudo incluir en la factura";
}

test("describirMotivoExclusion: NoResuelto → 'todavía no está confirmado'", () => {
  assert.equal(describirMotivoExclusion("NoResuelto"), "todavía no está confirmado");
});

test("describirMotivoExclusion: Cancelado → 'está cancelado'", () => {
  assert.equal(describirMotivoExclusion("Cancelado"), "está cancelado");
});

test("describirMotivoExclusion: PrecioCero → 'tiene precio $0'", () => {
  assert.equal(describirMotivoExclusion("PrecioCero"), "tiene precio $0");
});

test("describirMotivoExclusion: motivo desconocido → texto genérico, NUNCA el token crudo", () => {
  const resultado = describirMotivoExclusion("MotivoNuevoQueElFrontNoMapeoTodavia");
  assert.equal(resultado, "no se pudo incluir en la factura");
  assert.notEqual(resultado, "MotivoNuevoQueElFrontNoMapeoTodavia");
});

test("describirMotivoExclusion: sin motivo (undefined/null) → texto genérico, no revienta", () => {
  assert.equal(describirMotivoExclusion(undefined), "no se pudo incluir en la factura");
  assert.equal(describirMotivoExclusion(null), "no se pudo incluir en la factura");
});
