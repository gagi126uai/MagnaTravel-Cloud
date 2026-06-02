/**
 * Funciones puras para construir los payloads de penalidad y snapshot fiscal
 * del flujo de cancelacion de reservas (ADR-013/014).
 *
 * Separadas de CancelReservaModal.jsx para ser testeables con node:test sin DOM.
 * El modal las importa y las llama internamente — el comportamiento es identico
 * a antes de la extraccion, pero ahora los casos de negocio se pueden verificar
 * sin montar React.
 *
 * Funciones exportadas:
 *   - buildPenaltyClassificationPayload(selectedOption, agencyConceptKind, agencyPenaltyStatus, agencyPenaltyAmount)
 *   - buildSnapshotData(afipSettings)
 */

// ============================================================================
// Constantes de enums del backend.
//
// El backend NO tiene JsonStringEnumConverter en Program.cs (solo ReferenceHandler),
// por lo que System.Text.Json serializa/deserializa enums como INT.
// Si mandamos el string "OperatorPenaltyPassThrough" el backend da 400.
// Fuente verificada: CancellationConceptKind.cs, PenaltyStatus.cs, DebitNotePurpose.cs.
// ============================================================================

// CancellationConceptKind (verificado en CancellationConceptKind.cs)
export const CONCEPT_KIND = {
  OperatorPenaltyPassThrough: 0,
  AgencyManagementFee: 1,
  AgencyCancellationFee: 2,
  RealInsurancePremium: 3,
  AgencyCancellationCoverage: 4,
  AgencyInsuranceCommission: 5,
};

// PenaltyStatus (verificado en PenaltyStatus.cs)
export const PENALTY_STATUS = {
  Estimated: 0,
  Confirmed: 1,
};

// DebitNotePurpose (verificado en DebitNotePurpose.cs — MVP solo tiene este valor)
export const DEBIT_NOTE_PURPOSE = {
  PenaltyOrCancellationCharge: 0,
};

// ExchangeRateSource (verificado en ExchangeRateSource.cs)
// Para ARS (rate = 1) usamos BCRA_A3500 = 1: es un source oficial valido,
// no requiere ManualJustification (esa exigencia es solo para Manual = 5).
// Unset = 0 es ILEGAL para confirmar (el backend lo rechaza con INV-118).
export const EXCHANGE_RATE_SOURCE = {
  BCRA_A3500: 1,
  Manual: 5,
};

// ============================================================================
// buildPenaltyClassificationPayload
// ============================================================================

/**
 * Mapea la opcion de penalidad seleccionada en la UI a los campos del backend.
 *
 * Regla de negocio: el default conservador es "pass-through" (operador descuenta,
 * agencia no emite nada). La opcion "cargo propio" solo aparece si hay permiso+flag,
 * pero esta funcion no sabe de permisos — eso lo filtra el componente antes de
 * renderizar las opciones. Aca solo mapeamos la seleccion a los ints del backend.
 *
 * @param {string} selectedOption - "none" | "operator_pass_through" | "agency_charge" | "insurance"
 * @param {string} agencyConceptKind - string key del CONCEPT_KIND: "AgencyManagementFee" | "AgencyCancellationFee"
 * @param {string} agencyPenaltyStatus - "Estimated" | "Confirmed"
 * @param {string} agencyPenaltyAmount - string con el monto (puede ser "" o "0")
 * @returns {{ penaltyConceptKind: number, penaltyStatus: number|null, debitNotePurpose: number|null, confirmedPenaltyAmount: number|null }}
 */
export function buildPenaltyClassificationPayload(
  selectedOption,
  agencyConceptKind,
  agencyPenaltyStatus,
  agencyPenaltyAmount
) {
  switch (selectedOption) {
    case "none":
      // Sin penalidad: el operador devuelve todo. Pass-through = int 0.
      return {
        penaltyConceptKind: CONCEPT_KIND.OperatorPenaltyPassThrough,
        penaltyStatus: null,
        debitNotePurpose: null,
        confirmedPenaltyAmount: null,
      };

    case "agency_charge": {
      // Cargo propio de la agencia: puede emitir ND ahora (Confirmed) o despues (Estimated).
      // agencyConceptKind es la KEY del string del formulario ("AgencyManagementFee" etc).
      // Mapeamos al int que espera el backend via CONCEPT_KIND[key].
      const conceptKindInt = CONCEPT_KIND[agencyConceptKind] ?? CONCEPT_KIND.AgencyManagementFee;
      const penaltyStatusInt = PENALTY_STATUS[agencyPenaltyStatus] ?? PENALTY_STATUS.Estimated;
      const amount = parseFloat(agencyPenaltyAmount) || null;
      return {
        penaltyConceptKind: conceptKindInt,
        // PenaltyStatus como int (0=Estimated, 1=Confirmed).
        penaltyStatus: penaltyStatusInt,
        // DebitNotePurpose como int (0=PenaltyOrCancellationCharge, unico del MVP).
        debitNotePurpose: DEBIT_NOTE_PURPOSE.PenaltyOrCancellationCharge,
        // Solo mandamos el monto si esta confirmado. Si es Estimated, el backend lo
        // ignora y no emite ND hasta la confirmacion diferida (ADR-014).
        confirmedPenaltyAmount: agencyPenaltyStatus === "Confirmed" ? amount : null,
      };
    }

    case "insurance":
      // Seguro/cobertura: el backend lo rutea a revision manual (ManualReview).
      // Enviamos null en todos los campos de clasificacion (el backend usa sus defaults).
      return {
        penaltyConceptKind: null,
        penaltyStatus: null,
        debitNotePurpose: null,
        confirmedPenaltyAmount: null,
      };

    case "operator_pass_through":
    default:
      // DEFAULT: el operador retiene, la agencia no emite nada propio. Int 0 = OperatorPenaltyPassThrough.
      return {
        penaltyConceptKind: CONCEPT_KIND.OperatorPenaltyPassThrough,
        penaltyStatus: null,
        debitNotePurpose: null,
        confirmedPenaltyAmount: null,
      };
  }
}

// ============================================================================
// buildSnapshotData
// ============================================================================

/**
 * Construye el snapshot fiscal a partir de los settings de /afip/settings.
 * El snapshot congela las condiciones fiscales al momento del evento.
 *
 * IMPORTANTE — strings de condicion fiscal:
 *   El backend normaliza los strings con TaxConditionNormalizer (case-insensitive,
 *   sin tildes). Si alguno queda Unknown, rechaza con INV-118 (400/409).
 *   Los valores aceptados estan documentados en TaxConditionNormalizer.cs.
 *   Usamos los formatos exactos del "texto libre" (con espacio, sin guion bajo)
 *   que el normalizer reconoce para customer y supplier.
 *
 * FUENTE de las condiciones fiscales:
 *   - agencia: afipSettings.taxCondition (ej. "Monotributo", "Responsable Inscripto").
 *   - supplier: el MVP usa "Responsable Inscripto" como fallback razonable para ARS.
 *     TODO M2: cuando ReservaDto exponga supplierTaxCondition del proveedor principal,
 *     reemplazar este fallback derivando el valor de la reserva.
 *   - customer: el MVP usa "Consumidor Final" como fallback razonable.
 *     TODO M2: cuando CustomerDto exponga taxCondition, derivar de reserva.payer.taxCondition.
 *
 * FUENTE del tipo de cambio para ARS:
 *   Para pesos (ARS), rate = 1 y Source = BCRA_A3500 (int 1). No es "Manual" (5):
 *   Manual requiere manualJustification (INV-120), que no pedimos al agente para ARS.
 *   BCRA_A3500 es un source oficial valido para operaciones en pesos.
 *
 * @param {object|null} afipSettings - Respuesta de GET /afip/settings, o null si fallo el fetch.
 * @returns {object} Objeto listo para mandar como snapshotData en ConfirmCancellationRequest.
 */
export function buildSnapshotData(afipSettings) {
  return {
    currencyAtEvent: "ARS",
    exchangeRateAtOriginalInvoice: 1.0,
    // ExchangeRateSource int: BCRA_A3500 = 1 (NO es Manual = 5; Manual requiere justificacion).
    source: EXCHANGE_RATE_SOURCE.BCRA_A3500,
    manualJustification: null,
    // Condicion fiscal de la agencia: viene de /afip/settings.
    // El normalizer acepta "Monotributo" y "Responsable Inscripto" (con espacio).
    agencyTaxConditionAtEvent: afipSettings?.taxCondition || "Monotributo",
    // Condicion fiscal del proveedor: fallback "Responsable Inscripto" (con espacio).
    // El normalizer acepta este formato y lo convierte a "RESPONSABLE_INSCRIPTO".
    // "ResponsableInscripto" (sin espacio) NO lo acepta → INV-118.
    // TODO M2: deducir del proveedor de la reserva cuando el DTO lo exponga.
    supplierTaxConditionAtEvent: "Responsable Inscripto",
    // Condicion fiscal del cliente: fallback "Consumidor Final" (con espacio).
    // El normalizer acepta este formato y lo convierte a "CONSUMIDOR_FINAL".
    // "ConsumidorFinal" (sin espacio) NO lo acepta → INV-118.
    // TODO M2: deducir de reserva.payer.taxCondition cuando el CustomerDto lo exponga.
    customerTaxConditionAtEvent: "Consumidor Final",
  };
}
