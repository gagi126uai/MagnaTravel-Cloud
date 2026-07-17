/**
 * Lógica PURA de la solapa "Datos" de la ficha del cliente (espejo de la ficha del
 * operador, spec `docs/ux/2026-07-17-ficha-cliente-solapa-datos.md`).
 *
 * Se separa del JSX (`DatosClienteTab.jsx`) para poder testear las reglas de armado del
 * formulario, la validación de guardado y el candado del CUIT sin montar React ni DOM
 * (mismo criterio que `pendingPenaltiesLogic.js`).
 *
 * REGLA DE ORO (spec §4 y §6): el banner "faltan datos fiscales" y el candado del CUIT
 * son veredictos del BACKEND (`hasPendingTaxData` / `taxIdLocked`). Este archivo nunca
 * recalcula esos veredictos a partir de otros campos del cliente — solo lee el booleano
 * que ya vino armado.
 */

// Opciones del desplegable de condición fiscal (AFIP). Mismo orden y mismos códigos que
// ya usa `CustomerFormModal.jsx` (el modal del listado de clientes) — se reutiliza el
// mismo catálogo para que las dos pantallas nunca puedan divergir.
export const TAX_CONDITION_OPTIONS = [
  { value: 1, label: "Responsable Inscripto" },
  { value: 6, label: "Monotributo" },
  { value: 4, label: "Exento" },
  { value: 5, label: "Consumidor Final" },
];

// Consumidor Final: mismo default que usa el alta de cliente (CustomerFormModal y el
// alta del backend, CustomerService.CreateCustomerAsync).
const DEFAULT_TAX_CONDITION_ID = 5;

/**
 * Traduce el código numérico de condición fiscal a la etiqueta en criollo que ve el
 * usuario. Nunca se muestra el número crudo (`taxConditionId`) en la pantalla — eso
 * sería jerga técnica interna, no un dato de negocio.
 *
 * @param {number|null|undefined} taxConditionId
 * @returns {string}
 */
export function mapearCondicionFiscalATexto(taxConditionId) {
  const opcion = TAX_CONDITION_OPTIONS.find((item) => item.value === Number(taxConditionId));
  return opcion ? opcion.label : "—";
}

/**
 * Arma el estado inicial editable del formulario a partir de la respuesta de
 * `GET /customers/{id}` (campos `fullName`, `taxId`, `taxConditionId`, `documentNumber`,
 * `email`, `phone`, `address`, `isActive` — spec §7).
 *
 * `notes` NO forma parte de este formulario (esta solapa no lo muestra ni lo edita) y
 * por eso no vive acá: se guarda aparte en el componente y se reinyecta tal cual al
 * guardar (ver `construirPayloadDatosCliente`), para no perder ese dato.
 *
 * @param {object|null|undefined} customer - respuesta de GET /customers/{id}
 * @returns {{fullName: string, documentNumber: string, taxId: string, taxConditionId: number, email: string, phone: string, address: string, isActive: boolean}}
 */
export function construirEstadoInicialDatosCliente(customer) {
  return {
    fullName: customer?.fullName || "",
    documentNumber: customer?.documentNumber || "",
    taxId: customer?.taxId || "",
    taxConditionId: customer?.taxConditionId ?? DEFAULT_TAX_CONDITION_ID,
    email: customer?.email || "",
    phone: customer?.phone || "",
    address: customer?.address || "",
    isActive: customer?.isActive ?? true,
  };
}

/**
 * Únicos campos obligatorios para habilitar "Guardar cambios" (spec §2): Nombre completo
 * y Condición fiscal. El resto de las reglas de negocio (CUIT inválido, etc.) las valida
 * el backend — acá solo se evita un envío vacío que el usuario tendría que corregir igual.
 *
 * @param {{fullName?: string, taxConditionId?: number|null}} formData
 * @returns {boolean}
 */
export function puedeGuardarDatosCliente(formData) {
  const nombreCompleto = (formData?.fullName || "").trim();
  const tieneCondicionFiscal = formData?.taxConditionId !== null && formData?.taxConditionId !== undefined;
  return nombreCompleto.length > 0 && tieneCondicionFiscal;
}

/**
 * Arma el body del `PUT /customers/{id}` — el MISMO endpoint que ya usa el modal del
 * listado (`CustomerFormModal` / `useCustomers.js`), no hay endpoint nuevo (spec §7).
 *
 * Dos detalles no obvios:
 *   - Nunca manda `taxCondition` (el texto): el backend siempre lo deriva del código
 *     `taxConditionId` (fix del mismo día, `CustomerTaxConditionCatalog.ResolveIncoming`).
 *     Mandar los dos podría desincronizarlos si alguna vez no coinciden.
 *   - Reinyecta `notasOriginales` tal cual: esta solapa no muestra el campo `notes`, pero
 *     el backend lo pisa COMPLETO si el PUT no lo trae (`UpdateCustomerAsync` no lo
 *     preserva como sí hace con `documentNumber`) — sin este round-trip, guardar acá
 *     borraría en silencio cualquier nota cargada desde otro lado.
 *
 * @param {ReturnType<typeof construirEstadoInicialDatosCliente>} formData
 * @param {string|null|undefined} notasOriginales
 * @returns {object} body listo para `api.put`
 */
export function construirPayloadDatosCliente(formData, notasOriginales) {
  return {
    fullName: formData.fullName.trim(),
    email: formData.email,
    phone: formData.phone,
    documentNumber: formData.documentNumber,
    address: formData.address,
    notes: notasOriginales ?? null,
    taxId: formData.taxId,
    taxConditionId: formData.taxConditionId,
    isActive: formData.isActive,
  };
}

/**
 * Candado del CUIT (spec §3): SOLO el campo CUIT/DNI y el botón de búsqueda AFIP se
 * deshabilitan cuando el cliente ya tiene una factura viva con ese CUIT. Se expone como
 * función (en vez de comparar `=== true` repetido en el JSX) para que quede en un único
 * lugar testeable y no se pueda reintroducir por error la regla de calcularlo distinto.
 *
 * @param {boolean|null|undefined} taxIdLocked - veredicto del backend (overview.taxIdLocked)
 * @returns {boolean}
 */
export function debeDeshabilitarCuit(taxIdLocked) {
  return taxIdLocked === true;
}

/**
 * Banner ámbar "Faltan los datos fiscales de este cliente" (spec §4). Recibe SOLO el
 * booleano que ya calculó el backend — nunca el cliente completo ni `taxConditionId` —
 * para que sea imposible reintroducir la regla PROHIBIDA "`!taxConditionId` enciende el
 * banner" (el cliente siempre arranca en Consumidor Final por defecto, así que esa
 * condición nunca dispararía el caso real que el backend sí detecta: dato legacy roto).
 *
 * @param {boolean|null|undefined} hasPendingTaxData - veredicto del backend (overview.hasPendingTaxData)
 * @returns {boolean}
 */
export function debeMostrarBannerDatosFiscales(hasPendingTaxData) {
  return hasPendingTaxData === true;
}
