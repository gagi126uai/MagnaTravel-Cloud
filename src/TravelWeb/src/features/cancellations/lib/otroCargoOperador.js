/**
 * Lógica PURA de "Agregar otro cargo de este operador" (ADR-044 T4, spec
 * `docs/ux/2026-07-10-t4-multas-pantallas.md`, sección 1).
 *
 * Por qué existe esta acción: normalmente una multa de operador es UN solo cargo (el
 * cargo administrativo que ya crea `ConfirmarMultaOperadorInline`, sin preguntar nada
 * raro — regla "el caso simple NO cambia"). Pero el contador confirmó un caso real: el
 * MISMO operador a veces aplica DOS cosas a la vez sobre la misma anulación (ej. un
 * cargo administrativo Y una retención fiscal). Para ese caso hace falta poder sumar un
 * segundo cargo — acción secundaria, escondida detrás de un link discreto, que SÍ
 * pregunta el tipo de cargo (a diferencia del camino simple).
 *
 * Separado del componente (AgregarOtroCargoOperadorInline.jsx) para poder testear la
 * traducción de tokens del backend, la validación y el armado del payload sin DOM.
 *
 * La lógica de "a qué factura corresponde" (2+ facturas activas, ADR-044 T4) es
 * COMPARTIDA con ConfirmarMultaOperadorInline y ElegirFacturaDestinoInline — vive en
 * `facturaDestinoLogic.js`, no acá, para no duplicarla en cada formulario.
 */

import { hayFacturaDestinoAmbigua, facturaDestinoResuelta } from "./facturaDestinoLogic.js";

// Re-exportado por compatibilidad: AgregarOtroCargoOperadorInline y sus tests ya lo
// importan desde este archivo. La fuente de verdad es facturaDestinoLogic.js.
export { hayFacturaDestinoAmbigua };

// ============================================================================
// Constantes de enums del backend (AddOperatorChargeRequest).
//
// El backend NO tiene JsonStringEnumConverter (mismo criterio que penaltyPayload.js):
// los enums se mandan como INT, no como el nombre en texto. Fuente verificada:
// OperatorChargeKind.cs, PenaltyCollectionMode.cs, ClientTransferMode.cs.
// ============================================================================

// OperatorChargeKind (verificado en OperatorChargeKind.cs)
export const OPERATOR_CHARGE_KIND = {
  AdministrativeFee: 0,
  Tax: 1,
  Withholding: 2,
  Other: 3,
};

// PenaltyCollectionMode (verificado en PenaltyCollectionMode.cs)
export const PENALTY_COLLECTION_MODE = {
  Retenida: 0,
  FacturadaAparte: 1,
};

// ClientTransferMode (verificado en ClientTransferMode.cs)
export const CLIENT_TRANSFER_MODE = {
  AsIs: 0,
  WithManagementFee: 1,
  Absorbed: 2,
};

/**
 * Las 4 opciones de "Tipo de cargo" en el orden EXACTO de la spec ("cargo administrativo
 * · impuesto · retención fiscal · otro"), que coincide con el orden del enum del backend.
 * El primer valor es el default cuando se abre la ficha (P2=A: desplegable en español).
 */
export const TIPOS_CARGO = [
  { value: "AdministrativeFee", label: "Cargo administrativo" },
  { value: "Tax", label: "Impuesto" },
  { value: "Withholding", label: "Retención fiscal" },
  { value: "Other", label: "Otro" },
];

/** Moneda por defecto del segundo cargo: igual criterio que ConfirmarMultaOperadorInline (USD, los operadores turísticos suelen facturar en dólares). */
export const MONEDA_OTRO_CARGO_DEFAULT = "USD";

export const FUENTE_TIPO_CAMBIO = Object.freeze({
  BCRA_A3500: 1,
  Manual: 5,
  BNA_VendedorDivisa: 6,
});

/** Formato YYYY-MM-DD usando el calendario local, no UTC. */
export function fechaLocalInput(date = new Date()) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

/**
 * Traduce el token "Kind" del backend a la etiqueta en español que ya se muestra en el
 * desplegable (para reusar el mismo texto cuando el cargo ya cargado se lista en el
 * bloque de multa del operador).
 *
 * @param {string} kind - "AdministrativeFee" | "Tax" | "Withholding" | "Other"
 * @returns {string}
 */
export function etiquetaTipoCargo(kind) {
  const encontrado = TIPOS_CARGO.find((opcion) => opcion.value === kind);
  return encontrado ? encontrado.label : "Otro";
}

/**
 * Traduce el token "CollectionMode" del backend a un texto corto en español, para el
 * desglose de cargos del operador en la ficha (ADR-044 T4, fix F4, spec sección 1.2).
 * Mismo criterio de voz que el resto del panel: nunca el token crudo ("Retenida"/
 * "FacturadaAparte") — siempre la frase que ya usan los radios de "Más detalles".
 *
 * @param {string} collectionMode - "Retenida" | "FacturadaAparte"
 * @returns {string}
 */
export function etiquetaCollectionMode(collectionMode) {
  if (collectionMode === "FacturadaAparte") return "Te lo factura aparte";
  // "Retenida" es el default legacy (el único que existía antes de esta tanda) —
  // cualquier valor desconocido cae acá también (degradación segura).
  return "Lo descuenta de tu devolución";
}

/**
 * FIX F4 (2026-07-10, spec sección 1.2): true si hay que mostrar el desglose completo
 * de cargos del operador. Antes de este fix, después de agregar un segundo cargo NO
 * se veía nada distinto en la ficha — con 1 solo cargo (el caso simple, 100% de las
 * anulaciones de hoy) seguimos mostrando SOLO la línea resumen de siempre (monto +
 * moneda totales), sin este desglose fila por fila.
 *
 * @param {Array<object>} charges
 * @returns {boolean}
 */
export function debeMostrarDesgloseCargos(charges) {
  return Array.isArray(charges) && charges.length > 1;
}

/**
 * Arma las filas de texto listas para mostrar del desglose de cargos de un operador
 * (ADR-044 T4, fix F4). Respeta el enmascarado de costos: sin permiso
 * `cobranzas.see_cost`, `montoOculto` viene en `true` y el componente muestra "—" en
 * vez del monto real (nunca 0, nunca el monto verdadero) — mismo criterio que el resto
 * de la ficha y del extracto del operador.
 *
 * @param {Array<{kind: string, amount: number, currency: string, collectionMode: string, publicId?: string}>} charges
 * @param {boolean} puedeVerMontos
 * @returns {Array<{key: string, tipo: string, montoOculto: boolean, amount: number, currency: string, comoLoCobra: string}>}
 */
export function construirFilasDesgloseCargos(charges, puedeVerMontos) {
  return (Array.isArray(charges) ? charges : []).map((cargo, index) => ({
    key: cargo.publicId ?? `cargo-${index}`,
    tipo: etiquetaTipoCargo(cargo.kind),
    montoOculto: !puedeVerMontos,
    amount: cargo.amount,
    currency: cargo.currency,
    comoLoCobra: etiquetaCollectionMode(cargo.collectionMode),
  }));
}

/**
 * True si, con el modo de cobro elegido, hace falta pedir el documento del operador.
 * Regla dura del backend (AddOperatorChargeRequest): "Facturada aparte" EXIGE
 * DocumentRef; "Retenida" no lo necesita (el operador se lo descuenta del reembolso).
 *
 * @param {string} collectionMode - "Retenida" | "FacturadaAparte"
 * @returns {boolean}
 */
export function requiereDocumentoDelOperador(collectionMode) {
  return collectionMode === "FacturadaAparte";
}

/**
 * True si, con el traslado al cliente elegido, hace falta pedir el monto del cargo de
 * gestión. Solo aplica a "+ un cargo de gestión" (WithManagementFee) — el backend
 * rechaza el request si falta con ese modo, o si viene cargado con cualquier otro.
 *
 * @param {string} clientTransferMode - "AsIs" | "WithManagementFee" | "Absorbed"
 * @returns {boolean}
 */
export function requiereMontoDeGestion(clientTransferMode) {
  return clientTransferMode === "WithManagementFee";
}

/**
 * Valida los campos imprescindibles del formulario (monto + moneda), igual criterio que
 * `validarMonto` de ConfirmarMultaOperadorInline (se separó para no importar un
 * componente de otro).
 *
 * @param {string} montoStr
 * @returns {string|null}
 */
export function validarMontoOtroCargo(montoStr) {
  const monto = parseFloat(montoStr);
  if (!montoStr || isNaN(monto) || monto <= 0) {
    return "El monto debe ser mayor a cero.";
  }
  return null;
}

/**
 * Valida el campo de tipo de cambio estimado (obligatorio en los 3 datos cuando el
 * recuadro está visible: tipo de cambio + fuente + fecha).
 *
 * @param {{ mostrarRecuadro: boolean, tipoCambioStr: string, fuente: string, fecha: string, justificacion: string }} campos
 * @returns {{ tipoCambioError: string|null, fechaError: string|null, justificacionError: string|null }}
 */
export function validarTipoCambioEstimado({ mostrarRecuadro, tipoCambioStr, fuente, fecha, justificacion }) {
  if (!mostrarRecuadro) {
    return { tipoCambioError: null, fechaError: null, justificacionError: null };
  }

  const tipoCambio = parseFloat(tipoCambioStr);
  const tipoCambioError = !tipoCambioStr || isNaN(tipoCambio) || tipoCambio <= 0
    ? "El tipo de cambio debe ser mayor a cero."
    : null;

  const fechaError = !fecha ? "La fecha del tipo de cambio es obligatoria." : null;

  // INV-120 (mismo criterio que el resto del sistema): fuente "Manual" exige justificación.
  const justificacionError = fuente === "Manual" && !(justificacion ?? "").trim()
    ? "Con fuente \"Manual\" hace falta indicar de dónde salió el tipo de cambio."
    : null;

  return { tipoCambioError, fechaError, justificacionError };
}

/**
 * Determina si el formulario completo puede enviarse (sin errores, sin llamada en
 * curso). Junta todas las validaciones parciales para que el componente no tenga que
 * repetir el árbol de condiciones.
 *
 * @param {object} estado - Ver los campos usados abajo.
 * @returns {boolean}
 */
export function puedeAgregarOtroCargo({
  montoStr,
  collectionMode,
  documentRef,
  clientTransferMode,
  managementFeeAmountStr,
  mostrarRecuadroTipoCambio,
  tipoCambioStr,
  fuenteTipoCambio,
  fechaTipoCambio,
  justificacionTipoCambio,
  saleInvoices,
  targetInvoicePublicId,
  submitting,
}) {
  if (submitting) return false;
  if (validarMontoOtroCargo(montoStr) !== null) return false;

  if (requiereDocumentoDelOperador(collectionMode) && !(documentRef ?? "").trim()) {
    return false;
  }

  if (requiereMontoDeGestion(clientTransferMode)) {
    const montoGestion = parseFloat(managementFeeAmountStr);
    if (!managementFeeAmountStr || isNaN(montoGestion) || montoGestion <= 0) return false;
  }

  // P5 (2+ facturas activas): el botón queda apagado hasta elegir a cuál corresponde.
  // Con 1 sola factura, facturaDestinoResuelta siempre da true (no hay nada que elegir).
  if (!facturaDestinoResuelta(saleInvoices, targetInvoicePublicId)) return false;

  const { tipoCambioError, fechaError, justificacionError } = validarTipoCambioEstimado({
    mostrarRecuadro: mostrarRecuadroTipoCambio,
    tipoCambioStr,
    fuente: fuenteTipoCambio,
    fecha: fechaTipoCambio,
    justificacion: justificacionTipoCambio,
  });
  if (tipoCambioError || fechaError || justificacionError) return false;

  return true;
}

/**
 * Arma el payload de `POST /cancellations/{publicId}/operator-charges` (AddOperatorChargeRequest)
 * a partir del estado del formulario. Traduce las keys en español del formulario a los
 * INTs que espera el backend.
 *
 * `targetInvoicePublicId` (ADR-044 T4, 2026-07-10): solo se incluye cuando la reserva
 * tiene 2+ facturas activas (el usuario tuvo que elegir una). Con 1 sola factura no se
 * manda nada — el backend la autocompleta solo, comportamiento sin cambios.
 *
 * @param {object} form - Estado del formulario (ver los campos usados abajo).
 * @returns {object} - Shape de AddOperatorChargeRequest, listo para mandar por HTTP.
 */
export function construirPayloadOtroCargo({
  kind,
  montoStr,
  moneda,
  collectionMode,
  documentRef,
  notes,
  clientTransferMode,
  managementFeeAmountStr,
  mostrarRecuadroTipoCambio,
  tipoCambioStr,
  fuenteTipoCambio,
  fechaTipoCambio,
  justificacionTipoCambio,
  saleInvoices,
  targetInvoicePublicId,
}) {
  const payload = {
    kind: OPERATOR_CHARGE_KIND[kind] ?? OPERATOR_CHARGE_KIND.AdministrativeFee,
    collectionMode: PENALTY_COLLECTION_MODE[collectionMode] ?? PENALTY_COLLECTION_MODE.Retenida,
    amount: parseFloat(montoStr),
    currency: moneda,
    documentRef: requiereDocumentoDelOperador(collectionMode) ? (documentRef ?? "").trim() : null,
    notes: (notes ?? "").trim() || null,
    clientTransferMode: CLIENT_TRANSFER_MODE[clientTransferMode] ?? CLIENT_TRANSFER_MODE.AsIs,
    managementFeeAmount: requiereMontoDeGestion(clientTransferMode)
      ? parseFloat(managementFeeAmountStr)
      : null,
  };

  if (hayFacturaDestinoAmbigua(saleInvoices)) {
    payload.targetInvoicePublicId = targetInvoicePublicId;
  }

  if (mostrarRecuadroTipoCambio) {
    payload.estimatedExchangeRateToClientInvoiceCurrency = parseFloat(tipoCambioStr);
    payload.estimatedExchangeRateSource =
      FUENTE_TIPO_CAMBIO[fuenteTipoCambio] ?? FUENTE_TIPO_CAMBIO.Manual;
    payload.estimatedExchangeRateAt = fechaTipoCambio ? `${fechaTipoCambio}T00:00:00Z` : null;
    payload.estimatedExchangeRateJustification = (justificacionTipoCambio ?? "").trim() || null;
  }

  return payload;
}
