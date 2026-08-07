import { formatCurrency, formatDate } from "../../../lib/utils.js";

/**
 * Lógica pura del Tarifario nuevo ("la memoria de lo que ya vendiste", spec firmada
 * 2026-08-06 + fixes 2026-08-07). Vive separada de los componentes JSX para poder
 * testearla con `node --test`, sin levantar React.
 *
 * Cuatro cosas viven acá:
 *   1. Armar el payload de "+ Agregar producto" (contrato B, POST /rates/simple).
 *   2. Validar nombre/ciudad ANTES de mandar el alta o el renombre (mismo criterio en
 *      los dos casos: nombre siempre obligatorio, ciudad solo si es Hotel).
 *   3. Armar el texto de una fila de precio dentro de la ficha del producto
 *      ("Ola Mayorista · US$ 48 /noche · 22/05/2026").
 *   4. Armar el payload de renombrar un producto (POST /rates/learned-products/rename).
 *   5. Interpretar el resultado del cartel de repetidos (Usar existente / Crear nuevo /
 *      descartado) — fix 2026-08-07: antes CUALQUIER descarte (ESC, X, click afuera)
 *      caía por error en la rama de "crear igual" y duplicaba el producto.
 */

// Unidad que el motor espera para hotel ("por noche"); el resto de los tipos no manda
// unidad y deja que el motor la resuelva por tipo de servicio (ver CreateSimpleProductRequest).
const UNIDAD_PRECIO_HOTEL = "noche";

/**
 * Arma el payload de POST /rates/simple a partir de los valores de la fichita.
 * `createAnyway` se manda en true solo en el segundo intento, después de que el
 * usuario confirmó "Crear uno nuevo igual" ante el freno de repetidos (P7).
 */
export function buildCreateSimpleProductPayload(form, { createAnyway = false } = {}) {
  const esHotel = form.serviceType === "Hotel";
  return {
    serviceType: form.serviceType,
    name: (form.name || "").trim(),
    city: esHotel ? (form.city || "").trim() : null,
    supplierId: form.supplierId || null,
    price: Number(form.price) || 0,
    currency: form.currency || "ARS",
    priceUnit: esHotel ? UNIDAD_PRECIO_HOTEL : null,
    createAnyway,
  };
}

/**
 * Validación de usabilidad compartida por el alta ("+ Agregar producto") y el
 * renombre (ficha en línea): nombre siempre obligatorio, ciudad obligatoria SOLO para
 * Hotel (P-15: los obligatorios llevan asterisco y listo, nada de cartelitos
 * "opcional"). Esto NO reemplaza la validación del servidor — es solo para no mandar
 * un pedido que el backend va a rechazar por campos vacíos.
 */
export function validateProductNameAndCity({ serviceType, name, city }) {
  const errors = {};
  if (!(name || "").trim()) {
    errors.name = "Ingresá un nombre.";
  }
  if (serviceType === "Hotel" && !(city || "").trim()) {
    errors.city = "Ingresá una ciudad.";
  }
  return errors;
}

/**
 * Texto de una fila de precio dentro de la ficha del producto (§2.2):
 * "Ola Mayorista · US$ 48 /noche · 22/05/2026". La fecha llega ya calculada
 * (isOldPrice) desde el motor — este helper solo arma el texto, no decide antigüedad.
 */
export function buildSupplierPriceLineText(supplierPrice) {
  if (!supplierPrice) return "";
  const precio = formatCurrency(supplierPrice.price, supplierPrice.currency);
  const unidad = supplierPrice.priceUnitLabel ? ` ${supplierPrice.priceUnitLabel}` : "";
  const fecha = formatDate(supplierPrice.priceDate);
  const partes = [supplierPrice.supplierName, `${precio}${unidad}`];
  if (fecha && fecha !== "-") {
    partes.push(fecha);
  }
  return partes.join(" · ");
}

/**
 * Arma el payload de POST /rates/learned-products/rename. El producto se identifica
 * por su IDENTIDAD actual (tipo + nombre + ciudad), no por un id — porque un mismo
 * producto "aprendido" puede agrupar varias tarifas legacy con el mismo nombre/ciudad
 * (P2=A). `city`/`newCity` solo aplican a Hotel: en el resto de los tipos la ciudad no
 * es parte de la identidad del producto (ver §2.2).
 */
export function buildRenameLearnedProductPayload({ serviceType, currentName, currentCity, newName, newCity }) {
  const esHotel = serviceType === "Hotel";
  return {
    serviceType,
    name: (currentName || "").trim(),
    city: esHotel ? (currentCity || "").trim() : null,
    newName: (newName || "").trim(),
    newCity: esHotel ? (newCity || "").trim() : null,
  };
}

/** Las 3 salidas posibles del cartel de repetidos (§2.4, fix 2026-08-07). */
export const SIMILAR_PRODUCT_DIALOG_DECISION = {
  UseExisting: "useExisting",
  CreateNewAnyway: "createNewAnyway",
  Dismissed: "dismissed",
};

/**
 * Interpreta el resultado crudo del cartel ámbar (showConfirmWithAlternative, que
 * expone `isConfirmed`/`isDenied`/`isDismissed` de SweetAlert2 tal cual, sin traducir a
 * un booleano ambiguo) en una de las 3 decisiones de arriba.
 *
 * Bug que corrige (2026-08-07): el código viejo trataba TODO lo que no fuera el botón
 * "Usar existente" (incluido ESC, la X, click afuera del cartel) como si el usuario
 * hubiera elegido "Crear uno nuevo igual" — duplicando el producto sin que el usuario
 * lo pidiera. Acá, únicamente `isDenied` (el botón explícito "Crear uno nuevo igual")
 * dispara la creación; cualquier otro descarte es un no-op.
 */
export function resolveSimilarProductDialogDecision(dialogResult) {
  if (dialogResult?.isConfirmed) return SIMILAR_PRODUCT_DIALOG_DECISION.UseExisting;
  if (dialogResult?.isDenied) return SIMILAR_PRODUCT_DIALOG_DECISION.CreateNewAnyway;
  return SIMILAR_PRODUCT_DIALOG_DECISION.Dismissed;
}
