/**
 * Helpers para la cuenta corriente del cliente: cobros y métodos de pago.
 *
 * Módulo puro (sin React, sin dependencias de la app).
 * Se puede testear con `node --test` sin bundler.
 */

/**
 * Traduce el método de pago que llega del backend a una etiqueta en español.
 *
 * El campo `method` en CustomerAccountPaymentListItemDto puede venir en inglés
 * (los valores guardados por CustomerPaymentModal antes de la normalización)
 * o en español (valores guardados por RegistrarCobroInline y otros formularios más
 * nuevos). Este helper normaliza ambas variantes para que el extracto siempre
 * muestre español al usuario.
 *
 * Valores conocidos en el backend:
 *   Inglés (legado):  "Transfer" | "Cash" | "Card"
 *   Español (actual): "Transferencia" | "Efectivo" | "Tarjeta"
 *
 * Si el método es vacío o no reconocido, devuelve una cadena vacía para que
 * quien llama pueda decidir si omitir la parte del método o mostrar un genérico.
 * NUNCA devuelve el código técnico crudo al usuario.
 *
 * @param {string | null | undefined} method — valor crudo del DTO
 * @returns {string} — etiqueta en español, o "" si desconocido/vacío
 */
export function traducirMetodoPago(method) {
  if (!method) return "";

  const mapa = {
    // Inglés legado → español
    Transfer:       "Transferencia",
    Cash:           "Efectivo",
    Card:           "Tarjeta",
    // Español actual (pasa tal cual para no depender del orden de guardado)
    Transferencia:  "Transferencia",
    Efectivo:       "Efectivo",
    Tarjeta:        "Tarjeta",
    // Otros que puedan existir en la base de datos
    Cheque:         "Cheque",
    Check:          "Cheque",
    Other:          "",     // vacío = el llamador omite el método en la descripción
    Otro:           "",
  };

  // Normalización de case: "transfer" → lookup como "Transfer"
  const normalizado = method.charAt(0).toUpperCase() + method.slice(1).toLowerCase();

  // Intentar lookup exacto primero, luego capitalizado
  return mapa[method] ?? mapa[normalizado] ?? "";
}
