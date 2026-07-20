/**
 * Funciones puras para construir los payloads de penalidad del flujo de
 * cancelacion de reservas (ADR-013/014).
 *
 * Originalmente separadas de CancelReservaModal.jsx (retirado en la Tanda 3 del "contrato
 * pantalla-motor", 2026-07-20 — reemplazado por CancelarReservaInline.jsx) para ser
 * testeables con node:test sin DOM. CancelarReservaInline.jsx las importa y las llama
 * internamente hoy — el comportamiento es identico, los casos de negocio se pueden
 * verificar sin montar React.
 *
 * Funciones exportadas:
 *   - buildPenaltyClassificationPayload(selectedOption, agencyConceptKind, agencyPenaltyStatus, agencyPenaltyAmount)
 *
 * Tanda B (2026-07-16): se elimino buildSnapshotData de este archivo. Esa funcion armaba
 * el "snapshot fiscal" (condiciones fiscales + tipo de cambio) que el frontend le mandaba
 * al backend al confirmar una anulacion, pero lo hacia ADIVINANDO datos que no tenia (ej.
 * "Responsable Inscripto" fijo para el operador, sin importar la ficha real). El backend
 * ahora resuelve esos datos el mismo, directo de la base (BookingCancellationService.
 * ResolveServerSideTaxIdentity + la factura original) — el campo que se mandaba quedo
 * IGNORADO server-side. Ver docs/explicaciones de la Tanda B.
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
