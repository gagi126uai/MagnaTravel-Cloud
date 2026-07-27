/**
 * Mapas criollo para textos técnicos que el motor manda tal cual dentro del Libro de
 * Caja (hallazgos menores del barrido de estándares, firma de Gastón 2026-07-27):
 *
 *   a) Categoría de un movimiento AUTOMÁTICO generado sin intervención del usuario:
 *      "ClientCreditWithdrawal"/"ClientCreditReversal" (retiro/devolución de saldo a
 *      favor del cliente, `ManualCashMovementBuilder.BuildExpenseForWithdrawal`) y
 *      "OperatorRefund" (ingreso de una devolución recibida del operador,
 *      `ManualCashMovementBuilder.BuildIncomeForRefund`). Un cajero no programador no
 *      entiende esos tokens en inglés.
 *   b) Método de pago, cuando el backend lo manda como el nombre crudo del enum
 *      ("Cash"/"Transfer") en vez de texto ya traducido.
 *
 * REGLA DE ORO: las categorías/métodos que el usuario TIPEÓ A MANO (texto libre, ej.
 * "Cheque", "MercadoPago") NO están en estos mapas — el fallback los deja tal cual, no
 * hay forma de confundirlos con un token de sistema.
 *
 * Fix bloqueante de data-exposure (2026-07-27, verificado end-to-end por el reviewer):
 * "OperatorRefund" faltaba en este mapa — esas filas (ManualCashMovementBuilder.cs:119)
 * mostraban Categoría ("OperatorRefund") Y Método ("Transfer"/lo que informe el operador)
 * crudos en el modal de edición, con el lápiz habilitado (editables). Ver el test-guardia
 * más abajo, que obliga a mapear cualquier categoría de sistema NUEVA que sume el motor.
 */
import { traducirMetodoPago } from "../../customers/lib/paymentHelpers.js";

// Categorías que arma el motor SIN intervención del usuario (ver ManualCashMovementBuilder,
// switch de WithdrawalKind + BuildIncomeForRefund). Cualquier otra categoría es texto
// libre cargado a mano.
const CATEGORIAS_DE_SISTEMA = {
  ClientCreditWithdrawal: "Devolución de saldo al cliente",
  ClientCreditReversal: "Contra-asiento de devolución",
  OperatorRefund: "Devolución recibida del operador",
};

/**
 * True si la categoría es una de las que arma el motor automáticamente (no texto libre
 * del usuario). Se usa para decidir si el campo "Categoría" del modal de edición debe
 * quedar de solo lectura (editarla a mano rompería la trazabilidad de ese movimiento).
 *
 * @param {string|null|undefined} category
 * @returns {boolean}
 */
export function esCategoriaDeSistema(category) {
  return Object.prototype.hasOwnProperty.call(CATEGORIAS_DE_SISTEMA, category);
}

/**
 * Traduce una categoría de sistema a su texto en criollo. Categorías manuales (texto
 * libre del usuario) se devuelven TAL CUAL — nunca se inventa una traducción para algo
 * que el propio usuario escribió.
 *
 * @param {string|null|undefined} category
 * @returns {string}
 */
export function mapearCategoriaMovimiento(category) {
  return CATEGORIAS_DE_SISTEMA[category] || category || "";
}

// Fix bloqueante del reviewer (2026-07-27): `traducirMetodoPago` mapea "Other"/"Otro" a
// `""` A PROPÓSITO (el extracto de cuenta del cliente omite esa parte del texto en ese
// caso — ver su docstring). Acá, en cambio, un `""` en la columna Método se ve como una
// celda vacía (ambigua: ¿no hay dato cargado, o el método es "Otro"?). Por eso estos
// tokens CONOCIDOS-pero-sin-texto-propio se traducen a "Otro" ANTES de delegar a
// traducirMetodoPago, para no mostrar nunca el token crudo en inglés ("Other").
const CONOCIDOS_SIN_TEXTO = new Set(["Other", "Otro", "other", "otro"]);

/**
 * Traduce un método de pago crudo ("Cash"/"Transfer"/"Card"/"Other"/etc.) a su texto en
 * criollo. Reusa `traducirMetodoPago` (ya usado en el extracto de cuenta del cliente,
 * `features/customers/lib/paymentHelpers.js`) para no mantener un segundo mapa
 * Cash→Efectivo que se pueda desincronizar del que ya existe.
 *
 * Diferencia importante con `traducirMetodoPago` a secas: ese helper devuelve `""`
 * para "Other"/"Otro" Y para cualquier método NO reconocido (pensado para que el
 * extracto omita esa parte del texto). Acá, en cambio, la columna Método del Libro de
 * Caja necesita:
 *   1. Traducir "Other"/"Otro" a "Otro" (nunca mostrar el token crudo en inglés).
 *   2. Seguir mostrando el texto libre que el cajero tipeó a mano en un ajuste manual
 *      (ej. "MercadoPago", "Cheque #123") cuando NO es un token conocido — por eso, si
 *      `traducirMetodoPago` no reconoce el valor, se devuelve el método ORIGINAL tal
 *      cual, nunca vacío.
 *
 * @param {string|null|undefined} method
 * @returns {string}
 */
export function mapearMetodoMovimiento(method) {
  if (CONOCIDOS_SIN_TEXTO.has(method)) {
    return "Otro";
  }
  return traducirMetodoPago(method) || method || "";
}
