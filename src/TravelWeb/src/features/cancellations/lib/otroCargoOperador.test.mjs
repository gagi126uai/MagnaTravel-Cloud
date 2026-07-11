import { test } from "node:test";
import assert from "node:assert/strict";
import {
  TIPOS_CARGO,
  etiquetaTipoCargo,
  etiquetaCollectionMode,
  debeMostrarDesgloseCargos,
  construirFilasDesgloseCargos,
  requiereDocumentoDelOperador,
  requiereMontoDeGestion,
  hayFacturaDestinoAmbigua,
  validarMontoOtroCargo,
  validarTipoCambioEstimado,
  puedeAgregarOtroCargo,
  construirPayloadOtroCargo,
  OPERATOR_CHARGE_KIND,
  PENALTY_COLLECTION_MODE,
  CLIENT_TRANSFER_MODE,
  FUENTE_TIPO_CAMBIO,
  fechaLocalInput,
} from "./otroCargoOperador.js";

// monedaDeFacturaUnica y debeMostrarRecuadroTipoCambio se movieron a
// facturaDestinoLogic.js (compartidas con ConfirmarMultaOperadorInline y
// ElegirFacturaDestinoInline) — sus tests viven en facturaDestinoLogic.test.mjs.

test("TIPOS_CARGO respeta el orden exacto de la spec (administrativo, impuesto, retención, otro)", () => {
  assert.deepEqual(TIPOS_CARGO.map((o) => o.value), ["AdministrativeFee", "Tax", "Withholding", "Other"]);
});

test("etiquetaTipoCargo traduce cada token del backend a español", () => {
  assert.equal(etiquetaTipoCargo("AdministrativeFee"), "Cargo administrativo");
  assert.equal(etiquetaTipoCargo("Tax"), "Impuesto");
  assert.equal(etiquetaTipoCargo("Withholding"), "Retención fiscal");
  assert.equal(etiquetaTipoCargo("Other"), "Otro");
});

test("etiquetaCollectionMode: FacturadaAparte → 'Te lo factura aparte'", () => {
  assert.equal(etiquetaCollectionMode("FacturadaAparte"), "Te lo factura aparte");
});

test("etiquetaCollectionMode: Retenida (o cualquier valor desconocido) → 'Lo descuenta de tu devolución'", () => {
  assert.equal(etiquetaCollectionMode("Retenida"), "Lo descuenta de tu devolución");
  assert.equal(etiquetaCollectionMode("AlgoNuevoDelBackend"), "Lo descuenta de tu devolución");
  assert.equal(etiquetaCollectionMode(undefined), "Lo descuenta de tu devolución");
});

// ============================================================================
// FIX F4 (2026-07-10): desglose de cargos cuando hay más de uno (spec sección 1.2)
// ============================================================================

test("debeMostrarDesgloseCargos: false con 0 o 1 cargo (caso simple, sin cambios)", () => {
  assert.equal(debeMostrarDesgloseCargos([]), false);
  assert.equal(debeMostrarDesgloseCargos([{ kind: "AdministrativeFee" }]), false);
  assert.equal(debeMostrarDesgloseCargos(undefined), false);
});

test("debeMostrarDesgloseCargos: true con 2+ cargos (el caso real confirmado por el contador)", () => {
  const charges = [{ kind: "AdministrativeFee" }, { kind: "Withholding" }];
  assert.equal(debeMostrarDesgloseCargos(charges), true);
});

test("construirFilasDesgloseCargos: arma tipo + cómo lo cobra en español para cada cargo", () => {
  const charges = [
    { publicId: "charge-1", kind: "AdministrativeFee", amount: 200, currency: "USD", collectionMode: "Retenida" },
    { publicId: "charge-2", kind: "Withholding", amount: 25000, currency: "ARS", collectionMode: "FacturadaAparte" },
  ];
  const filas = construirFilasDesgloseCargos(charges, true);

  assert.equal(filas.length, 2);
  assert.equal(filas[0].key, "charge-1");
  assert.equal(filas[0].tipo, "Cargo administrativo");
  assert.equal(filas[0].comoLoCobra, "Lo descuenta de tu devolución");
  assert.equal(filas[0].amount, 200);
  assert.equal(filas[0].currency, "USD");
  assert.equal(filas[0].montoOculto, false);

  assert.equal(filas[1].tipo, "Retención fiscal");
  assert.equal(filas[1].comoLoCobra, "Te lo factura aparte");
});

test("construirFilasDesgloseCargos: enmascara el monto sin permiso cobranzas.see_cost", () => {
  const charges = [{ kind: "AdministrativeFee", amount: 200, currency: "USD", collectionMode: "Retenida" }];
  const filas = construirFilasDesgloseCargos(charges, false);
  assert.equal(filas[0].montoOculto, true);
});

test("construirFilasDesgloseCargos: sin publicId, usa un key defensivo por índice", () => {
  const charges = [{ kind: "AdministrativeFee", amount: 200, currency: "USD", collectionMode: "Retenida" }];
  const filas = construirFilasDesgloseCargos(charges, true);
  assert.equal(filas[0].key, "cargo-0");
});

test("construirFilasDesgloseCargos: lista vacía o ausente → [] (defensivo)", () => {
  assert.deepEqual(construirFilasDesgloseCargos([], true), []);
  assert.deepEqual(construirFilasDesgloseCargos(undefined, true), []);
});

test("etiquetaTipoCargo cae a 'Otro' ante un token desconocido (degradación segura)", () => {
  assert.equal(etiquetaTipoCargo("AlgoNuevoDelBackend"), "Otro");
});

test("fechaLocalInput usa los componentes locales sin convertir a UTC", () => {
  const fakeLocalDate = {
    getFullYear: () => 2026,
    getMonth: () => 6,
    getDate: () => 10,
  };
  assert.equal(fechaLocalInput(fakeLocalDate), "2026-07-10");
});

test("requiereDocumentoDelOperador: solo 'FacturadaAparte' exige el documento", () => {
  assert.equal(requiereDocumentoDelOperador("FacturadaAparte"), true);
  assert.equal(requiereDocumentoDelOperador("Retenida"), false);
});

test("requiereMontoDeGestion: solo 'WithManagementFee' exige el monto del cargo de gestión", () => {
  assert.equal(requiereMontoDeGestion("WithManagementFee"), true);
  assert.equal(requiereMontoDeGestion("AsIs"), false);
  assert.equal(requiereMontoDeGestion("Absorbed"), false);
});

test("hayFacturaDestinoAmbigua: true solo con 2+ facturas activas", () => {
  assert.equal(hayFacturaDestinoAmbigua([]), false);
  assert.equal(hayFacturaDestinoAmbigua([{ currency: "USD" }]), false);
  assert.equal(hayFacturaDestinoAmbigua([{ currency: "USD" }, { currency: "ARS" }]), true);
});

test("validarMontoOtroCargo rechaza vacío, cero y negativo", () => {
  assert.equal(validarMontoOtroCargo(""), "El monto debe ser mayor a cero.");
  assert.equal(validarMontoOtroCargo("0"), "El monto debe ser mayor a cero.");
  assert.equal(validarMontoOtroCargo("-5"), "El monto debe ser mayor a cero.");
  assert.equal(validarMontoOtroCargo("100"), null);
});

test("validarTipoCambioEstimado: sin recuadro visible, no hay errores", () => {
  const resultado = validarTipoCambioEstimado({
    mostrarRecuadro: false,
    tipoCambioStr: "",
    fuente: "Manual",
    fecha: "",
    justificacion: "",
  });
  assert.deepEqual(resultado, { tipoCambioError: null, fechaError: null, justificacionError: null });
});

test("validarTipoCambioEstimado: con recuadro visible, los 3 datos son obligatorios", () => {
  const resultado = validarTipoCambioEstimado({
    mostrarRecuadro: true,
    tipoCambioStr: "",
    fuente: "Manual",
    fecha: "",
    justificacion: "",
  });
  assert.ok(resultado.tipoCambioError);
  assert.ok(resultado.fechaError);
  assert.ok(resultado.justificacionError);
});

test("validarTipoCambioEstimado: fuente Manual exige justificación; otra fuente no", () => {
  const conManual = validarTipoCambioEstimado({
    mostrarRecuadro: true,
    tipoCambioStr: "1200",
    fuente: "Manual",
    fecha: "2026-07-10",
    justificacion: "",
  });
  assert.ok(conManual.justificacionError);

  const conBna = validarTipoCambioEstimado({
    mostrarRecuadro: true,
    tipoCambioStr: "1200",
    fuente: "BNA_VendedorDivisa",
    fecha: "2026-07-10",
    justificacion: "",
  });
  assert.equal(conBna.justificacionError, null);
});

test("puedeAgregarOtroCargo: caso simple (retenida, tal cual, sin cruce de moneda) habilita con solo monto", () => {
  const habilitado = puedeAgregarOtroCargo({
    montoStr: "500",
    collectionMode: "Retenida",
    documentRef: "",
    clientTransferMode: "AsIs",
    managementFeeAmountStr: "",
    mostrarRecuadroTipoCambio: false,
    tipoCambioStr: "",
    fuenteTipoCambio: "Manual",
    fechaTipoCambio: "",
    justificacionTipoCambio: "",
    submitting: false,
  });
  assert.equal(habilitado, true);
});

test("puedeAgregarOtroCargo: bloquea si 'Facturada aparte' sin documento", () => {
  const habilitado = puedeAgregarOtroCargo({
    montoStr: "500",
    collectionMode: "FacturadaAparte",
    documentRef: "",
    clientTransferMode: "AsIs",
    managementFeeAmountStr: "",
    mostrarRecuadroTipoCambio: false,
    tipoCambioStr: "",
    fuenteTipoCambio: "Manual",
    fechaTipoCambio: "",
    justificacionTipoCambio: "",
    submitting: false,
  });
  assert.equal(habilitado, false);
});

test("puedeAgregarOtroCargo: bloquea si '+ cargo de gestión' sin el monto de gestión", () => {
  const habilitado = puedeAgregarOtroCargo({
    montoStr: "500",
    collectionMode: "Retenida",
    documentRef: "",
    clientTransferMode: "WithManagementFee",
    managementFeeAmountStr: "",
    mostrarRecuadroTipoCambio: false,
    tipoCambioStr: "",
    fuenteTipoCambio: "Manual",
    fechaTipoCambio: "",
    justificacionTipoCambio: "",
    submitting: false,
  });
  assert.equal(habilitado, false);
});

test("puedeAgregarOtroCargo: con 2+ facturas activas, bloquea hasta elegir la factura destino (P5)", () => {
  const saleInvoices = [{ publicId: "inv-1", currency: "USD" }, { publicId: "inv-2", currency: "ARS" }];
  const sinElegir = puedeAgregarOtroCargo({
    montoStr: "500",
    collectionMode: "Retenida",
    documentRef: "",
    clientTransferMode: "AsIs",
    managementFeeAmountStr: "",
    mostrarRecuadroTipoCambio: false,
    tipoCambioStr: "",
    fuenteTipoCambio: "Manual",
    fechaTipoCambio: "",
    justificacionTipoCambio: "",
    saleInvoices,
    targetInvoicePublicId: null,
    submitting: false,
  });
  assert.equal(sinElegir, false);

  const conElegida = puedeAgregarOtroCargo({
    montoStr: "500",
    collectionMode: "Retenida",
    documentRef: "",
    clientTransferMode: "AsIs",
    managementFeeAmountStr: "",
    mostrarRecuadroTipoCambio: false,
    tipoCambioStr: "",
    fuenteTipoCambio: "Manual",
    fechaTipoCambio: "",
    justificacionTipoCambio: "",
    saleInvoices,
    targetInvoicePublicId: "inv-2",
    submitting: false,
  });
  assert.equal(conElegida, true);
});

test("puedeAgregarOtroCargo: bloquea mientras está enviando (submitting)", () => {
  const habilitado = puedeAgregarOtroCargo({
    montoStr: "500",
    collectionMode: "Retenida",
    documentRef: "",
    clientTransferMode: "AsIs",
    managementFeeAmountStr: "",
    mostrarRecuadroTipoCambio: false,
    tipoCambioStr: "",
    fuenteTipoCambio: "Manual",
    fechaTipoCambio: "",
    justificacionTipoCambio: "",
    submitting: true,
  });
  assert.equal(habilitado, false);
});

test("construirPayloadOtroCargo: caso simple mapea a los INT correctos y no manda TC", () => {
  const payload = construirPayloadOtroCargo({
    kind: "Withholding",
    montoStr: "25000",
    moneda: "ARS",
    collectionMode: "Retenida",
    documentRef: "",
    notes: "",
    clientTransferMode: "AsIs",
    managementFeeAmountStr: "",
    mostrarRecuadroTipoCambio: false,
    tipoCambioStr: "",
    fuenteTipoCambio: "Manual",
    fechaTipoCambio: "",
    justificacionTipoCambio: "",
  });

  assert.equal(payload.kind, OPERATOR_CHARGE_KIND.Withholding);
  assert.equal(payload.collectionMode, PENALTY_COLLECTION_MODE.Retenida);
  assert.equal(payload.amount, 25000);
  assert.equal(payload.currency, "ARS");
  assert.equal(payload.documentRef, null);
  assert.equal(payload.clientTransferMode, CLIENT_TRANSFER_MODE.AsIs);
  assert.equal(payload.managementFeeAmount, null);
  assert.equal("estimatedExchangeRateToClientInvoiceCurrency" in payload, false);
});

test("construirPayloadOtroCargo: 'Facturada aparte' incluye el documentRef trimeado", () => {
  const payload = construirPayloadOtroCargo({
    kind: "Tax",
    montoStr: "100",
    moneda: "USD",
    collectionMode: "FacturadaAparte",
    documentRef: "  ND-0001-000123  ",
    notes: "",
    clientTransferMode: "AsIs",
    managementFeeAmountStr: "",
    mostrarRecuadroTipoCambio: false,
    tipoCambioStr: "",
    fuenteTipoCambio: "Manual",
    fechaTipoCambio: "",
    justificacionTipoCambio: "",
  });

  assert.equal(payload.documentRef, "ND-0001-000123");
});

test("construirPayloadOtroCargo: con recuadro de TC visible, manda los 4 campos de tipo de cambio", () => {
  const payload = construirPayloadOtroCargo({
    kind: "AdministrativeFee",
    montoStr: "200",
    moneda: "USD",
    collectionMode: "Retenida",
    documentRef: "",
    notes: "",
    clientTransferMode: "AsIs",
    managementFeeAmountStr: "",
    mostrarRecuadroTipoCambio: true,
    tipoCambioStr: "1200",
    fuenteTipoCambio: "Manual",
    fechaTipoCambio: "2026-07-10",
    justificacionTipoCambio: "TC del día que el operador cobró la multa",
  });

  assert.equal(payload.estimatedExchangeRateToClientInvoiceCurrency, 1200);
  assert.equal(payload.estimatedExchangeRateSource, 5); // Manual = 5
  assert.equal(payload.estimatedExchangeRateAt, "2026-07-10T00:00:00Z");
  assert.equal(payload.estimatedExchangeRateJustification, "TC del día que el operador cobró la multa");
});

test("construirPayloadOtroCargo preserva la fuente BNA vendedor divisa (enum 6)", () => {
  const payload = construirPayloadOtroCargo({
    kind: "AdministrativeFee",
    montoStr: "200",
    moneda: "USD",
    collectionMode: "Retenida",
    documentRef: "",
    notes: "",
    clientTransferMode: "AsIs",
    managementFeeAmountStr: "",
    mostrarRecuadroTipoCambio: true,
    tipoCambioStr: "1200",
    fuenteTipoCambio: "BNA_VendedorDivisa",
    fechaTipoCambio: "2026-07-10",
    justificacionTipoCambio: "",
  });

  assert.equal(FUENTE_TIPO_CAMBIO.BNA_VendedorDivisa, 6);
  assert.equal(payload.estimatedExchangeRateSource, 6);
});

test("construirPayloadOtroCargo preserva la fuente BCRA A3500 (enum 1)", () => {
  const payload = construirPayloadOtroCargo({
    kind: "AdministrativeFee",
    montoStr: "200",
    moneda: "USD",
    collectionMode: "Retenida",
    documentRef: "",
    notes: "",
    clientTransferMode: "AsIs",
    managementFeeAmountStr: "",
    mostrarRecuadroTipoCambio: true,
    tipoCambioStr: "1200",
    fuenteTipoCambio: "BCRA_A3500",
    fechaTipoCambio: "2026-07-10",
    justificacionTipoCambio: "",
  });

  assert.equal(payload.estimatedExchangeRateSource, 1);
});

test("construirPayloadOtroCargo: con 2+ facturas activas, incluye el targetInvoicePublicId elegido", () => {
  const payload = construirPayloadOtroCargo({
    kind: "AdministrativeFee",
    montoStr: "200",
    moneda: "USD",
    collectionMode: "Retenida",
    documentRef: "",
    notes: "",
    clientTransferMode: "AsIs",
    managementFeeAmountStr: "",
    mostrarRecuadroTipoCambio: false,
    tipoCambioStr: "",
    fuenteTipoCambio: "Manual",
    fechaTipoCambio: "",
    justificacionTipoCambio: "",
    saleInvoices: [{ publicId: "inv-1", currency: "USD" }, { publicId: "inv-2", currency: "ARS" }],
    targetInvoicePublicId: "inv-2",
  });

  assert.equal(payload.targetInvoicePublicId, "inv-2");
});

test("construirPayloadOtroCargo: con 1 sola factura, NO manda targetInvoicePublicId (se autocompleta sola)", () => {
  const payload = construirPayloadOtroCargo({
    kind: "AdministrativeFee",
    montoStr: "200",
    moneda: "USD",
    collectionMode: "Retenida",
    documentRef: "",
    notes: "",
    clientTransferMode: "AsIs",
    managementFeeAmountStr: "",
    mostrarRecuadroTipoCambio: false,
    tipoCambioStr: "",
    fuenteTipoCambio: "Manual",
    fechaTipoCambio: "",
    justificacionTipoCambio: "",
    saleInvoices: [{ publicId: "inv-1", currency: "USD" }],
    targetInvoicePublicId: null,
  });

  assert.equal("targetInvoicePublicId" in payload, false);
});

test("construirPayloadOtroCargo: '+ cargo de gestión' incluye el monto de gestión", () => {
  const payload = construirPayloadOtroCargo({
    kind: "AdministrativeFee",
    montoStr: "200",
    moneda: "ARS",
    collectionMode: "Retenida",
    documentRef: "",
    notes: "",
    clientTransferMode: "WithManagementFee",
    managementFeeAmountStr: "50",
    mostrarRecuadroTipoCambio: false,
    tipoCambioStr: "",
    fuenteTipoCambio: "Manual",
    fechaTipoCambio: "",
    justificacionTipoCambio: "",
  });

  assert.equal(payload.clientTransferMode, CLIENT_TRANSFER_MODE.WithManagementFee);
  assert.equal(payload.managementFeeAmount, 50);
});
