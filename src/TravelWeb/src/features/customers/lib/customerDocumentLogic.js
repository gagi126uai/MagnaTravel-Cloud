/**
 * Lógica PURA del casillero de documento unificado del alta de cliente (P1, mockup A
 * firmado 2026-07-25 — decisión firmada de Gastón, barrido E2E). Se separa del JSX
 * (`CustomerFormModal.jsx`) para poder testear el mapeo sin montar React, mismo criterio
 * que `datosClienteLogic.js`.
 *
 * EL PROBLEMA QUE RESUELVE (hallazgo H3): antes había DOS casilleros sueltos
 * ("Documento/Pasaporte" → documentNumber, y "CUIT/Documento" → taxId), y el form NUNCA
 * mandaba `documentType`. El guard de duplicados del motor (`CustomerService.CreateCustomerAsync`)
 * solo compara por `documentType`+`documentNumber` — sin `documentType`, ese guard nunca se
 * disparaba, aunque el motor ya lo tuviera programado.
 *
 * LA SOLUCIÓN: un solo casillero con desplegable de tipo (CUIT/CUIL/DNI/Pasaporte/Otro) +
 * número. Esta pantalla arma SIEMPRE `documentType`+`documentNumber` con lo que el usuario
 * cargó (revive el guard de duplicados para TODOS los tipos), y además copia el número a
 * `taxId` cuando el tipo es fiscal (CUIT/CUIL) — `taxId` es el campo que usa ARCA para
 * facturar (ver `ArcaReceptorResolver`, que prioriza `taxId` sobre `documentType` cuando
 * hay un CUIT válido cargado).
 */

// Los 5 tipos del mockup firmado, en el orden en que aparecen en el desplegable.
export const DOCUMENT_TYPE_OPTIONS = [
    { value: "CUIT", label: "CUIT" },
    { value: "CUIL", label: "CUIL" },
    { value: "DNI", label: "DNI" },
    { value: "Pasaporte", label: "Pasaporte" },
    { value: "Otro", label: "Otro" },
];

// Tipos que además son una identidad fiscal (van a taxId, el campo que usa ARCA/AFIP
// para facturar). CUIL comparte el mismo algoritmo de dígito verificador que el CUIT.
const TIPOS_FISCALES = new Set(["CUIT", "CUIL"]);

/**
 * True si el tipo de documento tiene sentido buscarlo en el padrón AFIP (mockup firmado:
 * la lupita solo aparece para CUIT/CUIL/DNI — un pasaporte extranjero o "Otro" no están
 * en el padrón argentino).
 *
 * @param {string} tipoDocumento
 * @returns {boolean}
 */
export function tipoDocumentoTieneBusquedaAfip(tipoDocumento) {
    return tipoDocumento === "CUIT" || tipoDocumento === "CUIL" || tipoDocumento === "DNI";
}

/**
 * True si el tipo de documento es una identidad fiscal (su número va a `taxId`, no solo
 * a `documentNumber`).
 *
 * @param {string} tipoDocumento
 * @returns {boolean}
 */
export function esTipoDocumentoFiscal(tipoDocumento) {
    return TIPOS_FISCALES.has(tipoDocumento);
}

/**
 * Arma el estado inicial del casillero único a partir de un cliente existente (edición)
 * o de la nada (alta). Regla de precedencia: si el cliente ya tiene `taxId` cargado, ese
 * es el dato fiscal fuerte (mismo criterio que `ArcaReceptorResolver.ResolveDocument` en
 * el motor) y el casillero arranca en CUIT/CUIL con ese número. Si no, se usa
 * `documentType`/`documentNumber` tal cual estén. Sin ningún dato, arranca en DNI vacío
 * (mismo default que ya usaba el casillero de "Documento/Pasaporte" antes de unificarse).
 *
 * @param {{taxId?: string|null, documentType?: string|null, documentNumber?: string|null}|null|undefined} customer
 * @returns {{tipoDocumento: string, numeroDocumento: string}}
 */
export function construirEstadoInicialDocumento(customer) {
    const taxId = (customer?.taxId || "").trim();
    if (taxId) {
        const tipo = customer?.documentType === "CUIL" ? "CUIL" : "CUIT";
        return { tipoDocumento: tipo, numeroDocumento: taxId };
    }

    if (customer?.documentType) {
        return {
            tipoDocumento: customer.documentType,
            numeroDocumento: customer.documentNumber || "",
        };
    }

    return { tipoDocumento: "DNI", numeroDocumento: customer?.documentNumber || "" };
}

// Copia tal cual los tres campos de documento que ya tenía guardados el cliente, sin
// interpretar nada nuevo. Se usa cuando el casillero NO fue tocado (hallazgo B2, ver
// abajo) y también como base para "generar de la nada" en el alta (clienteOriginal
// undefined → los tres quedan null, mismo comportamiento que siempre tuvo el alta).
function valoresOriginales(clienteOriginal) {
    return {
        documentType: clienteOriginal?.documentType || null,
        documentNumber: clienteOriginal?.documentNumber || null,
        // OJO con "||" acá: un cliente legacy puede tener taxId="" guardado (todos los
        // clientes cargados por la versión anterior del modal, antes de que existiera este
        // casillero unificado). "||" convierte esa cadena vacía en null, y el motor compara
        // taxId con Ordinal.Equals — null !== "" dispara un taxIdChanged falso al tocar
        // SOLO el teléfono, con 409 (si hay factura con CAE) o auditoría fantasma como
        // consecuencia (BL-1, revisión 2026-07-27). "??" solo cae a null cuando el dato es
        // undefined/null de verdad, preservando "" tal cual estaba guardado.
        taxId: clienteOriginal?.taxId ?? null,
    };
}

// True si el cliente YA tenía cargado un documento NO fiscal real (DNI/Pasaporte/Otro
// con número) que hay que proteger de un pisado silencioso al guardar un CUIT/CUIL
// nuevo. Si no hay nada que proteger (alta nueva, o el cliente ya tenía CUIT/CUIL antes),
// no hace falta vaciar documentType/documentNumber — al contrario, conviene llenarlos
// con el CUIT/CUIL nuevo para que el guard de duplicados del motor (hallazgo H3,
// `CustomerService.CreateCustomerAsync`) pueda seguir comparando por esos dos campos.
function tieneDocumentoNoFiscalQueProteger(clienteOriginal) {
    const tipoOriginal = clienteOriginal?.documentType;
    const numeroOriginal = (clienteOriginal?.documentNumber || "").trim();
    if (!tipoOriginal || !numeroOriginal) return false;
    return !esTipoDocumentoFiscal(tipoOriginal);
}

/**
 * Mapea el casillero único a los tres campos que espera `CustomerUpsertRequest` en el
 * motor (`documentType`, `documentNumber`, `taxId`).
 *
 * HALLAZGO B2 (revisión 2026-07-27): el casillero único solo "ve" un tipo/número a la
 * vez (por ejemplo, si el cliente tiene CUIT + un DNI viejo cargado, el casillero
 * arranca mostrando el CUIT — ver `construirEstadoInicialDocumento`). Si al guardar
 * mandábamos SIEMPRE documentType/documentNumber/taxId derivados del casillero, un
 * cliente que abría la ficha solo para cambiar el teléfono terminaba PISANDO en
 * silencio el DNI que ya tenía guardado (el motor, `CustomerService`, preserva
 * documentType/documentNumber cuando llegan vacíos, pero pisa taxId siempre que
 * llega algo — así que hay que ser cuidadoso con qué se manda en cada caso).
 *
 * Reglas (en orden):
 *   1. Si el usuario NO tocó el casillero (ni cambió tipo ni número respecto de como
 *      arrancó la ficha) → se reenvían los 3 campos EXACTAMENTE como estaban guardados,
 *      sin ninguna interpretación nueva. Esto es lo que evita el pisado en silencio.
 *   2. Si lo tocó y lo dejó vacío → se borra todo (mismo comportamiento de siempre).
 *   3. Si lo tocó y el tipo elegido es fiscal (CUIT/CUIL):
 *        a. si el cliente YA tenía un DNI/Pasaporte/Otro real cargado (distinto de un
 *           CUIT/CUIL) → documentType/documentNumber viajan VACÍOS a propósito (el
 *           motor los preserva cuando llegan vacíos, así ese documento viejo no se
 *           pierde) — este es el caso que dispara el hallazgo B2.
 *        b. si no había nada que proteger (alta nueva, o el cliente ya tenía CUIT/CUIL
 *           antes) → documentType/documentNumber SÍ se llenan con el mismo valor que
 *           `taxId`, para que el guard de duplicados del motor (hallazgo H3) siga
 *           comparando por esos dos campos — si acá también los vaciáramos, un alta con
 *           CUIT (el caso más común) dejaría MUERTO ese guard otra vez.
 *   4. Si lo tocó y el tipo elegido NO es fiscal (DNI/Pasaporte/Otro) → va a
 *      documentType/documentNumber; `taxId` se reenvía IGUAL AL ORIGINAL (el motor lo
 *      pisa incondicional, así que si no lo reenviamos se borraría el CUIT sin que el
 *      usuario lo haya tocado).
 *
 * @param {{tipoDocumento: string, numeroDocumento: string, documentoFueTocado: boolean, clienteOriginal?: {documentType?:string|null, documentNumber?:string|null, taxId?:string|null}|null}} params
 * @returns {{documentType: string|null, documentNumber: string|null, taxId: string|null}}
 */
export function construirPayloadDocumento({ tipoDocumento, numeroDocumento, documentoFueTocado, clienteOriginal }) {
    if (!documentoFueTocado) {
        return valoresOriginales(clienteOriginal);
    }

    const numeroLimpio = (numeroDocumento || "").trim();

    if (!numeroLimpio) {
        return { documentType: null, documentNumber: null, taxId: null };
    }

    if (esTipoDocumentoFiscal(tipoDocumento)) {
        if (tieneDocumentoNoFiscalQueProteger(clienteOriginal)) {
            return { documentType: null, documentNumber: null, taxId: numeroLimpio };
        }
        return { documentType: tipoDocumento, documentNumber: numeroLimpio, taxId: numeroLimpio };
    }

    return {
        documentType: tipoDocumento,
        documentNumber: numeroLimpio,
        // Mismo motivo que en `valoresOriginales`: "??" en vez de "||" para no convertir un
        // taxId="" legacy en null y disparar un cambio falso (BL-1).
        taxId: clienteOriginal?.taxId ?? null,
    };
}

// Compara dos números de documento IGNORANDO guiones/espacios (fix del reviewer,
// verificación visual en PROD 2026-07-27, caso real "JAIR"): un CUIT puede estar
// guardado con guiones en un campo ("20-36053656-5") y sin guiones en el otro
// ("20360536565") — son el MISMO número, y compararlos con `===` a secas los trataba
// como dos documentos distintos por error.
function sonElMismoNumero(numeroA, numeroB) {
    const normalizar = (valor) => (valor || "").replace(/[-\s]/g, "");
    return normalizar(numeroA) === normalizar(numeroB);
}

/**
 * Devuelve el documento GUARDADO que el casillero único NO está mostrando, para pintarlo
 * como dato de SOLO LECTURA en la ficha del cliente (Obra 3, firma de Gastón 2026-07-27:
 * "si el cliente tiene el OTRO documento guardado (CUIT y DNI a la vez, hay 5 casos
 * reales), se muestra debajo... No se esconde ningún documento").
 *
 * Como `construirEstadoInicialDocumento` SIEMPRE prioriza `taxId` cuando existe (ver su
 * docstring: "taxId manda"), el caso MÁS COMÚN es: el casillero muestra CUIT/CUIL (por el
 * taxId) y, ADEMÁS, el cliente tiene un documento NO fiscal (DNI/Pasaporte/Otro) guardado
 * aparte en `documentType`/`documentNumber`. El caso inverso (casillero en DNI con un
 * CUIT oculto) no puede darse con el modelo actual: si hubiera un taxId cargado,
 * `construirEstadoInicialDocumento` ya lo mostraría a él, no al DNI.
 *
 * Fix bloqueante del reviewer (2026-07-27, primera vuelta): había un caso borde que este
 * helper escondía por error — un cliente legacy con `documentType` FISCAL (CUIT/CUIL)
 * pero con un `documentNumber` DISTINTO del `taxId` vigente (dato inconsistente real, no
 * un duplicado). Se agregó la comparación por NÚMERO, no solo por tipo.
 *
 * Fix bloqueante del reviewer (2026-07-27, verificación visual en PROD — caso real
 * "JAIR"): quedaba un TERCER caso borde. Un cliente legacy puede tener `taxId` cargado,
 * un `documentNumber` real y DISTINTO, pero `documentType` en `NULL` (dato legacy
 * incompleto, nunca se llegó a cargar el tipo). La guarda de arriba exigía
 * `documentType` truthy ANTES de comparar los números — con `documentType` null, la
 * función cortaba camino y devolvía `null` sin siquiera mirar el número, y ese DNI
 * quedaba invisible en toda la ficha. Ahora `documentType` es OPCIONAL: alcanza con
 * tener `taxId` + `documentNumber` real y distinto para mostrar el alternativo, con
 * tipo `null` cuando no hay dato de tipo (ver `describirDocumentoAlternativo`, que arma
 * la frase genérica "un documento ..." para ese caso).
 *
 * @param {{taxId?: string|null, documentType?: string|null, documentNumber?: string|null}|null|undefined} customer
 * @returns {{tipoDocumento: string|null, numeroDocumento: string}|null}
 */
export function obtenerDocumentoAlternativo(customer) {
    const taxId = (customer?.taxId || "").trim();
    const documentType = customer?.documentType || null;
    const documentNumber = (customer?.documentNumber || "").trim();

    if (!taxId || !documentNumber) {
        return null;
    }

    if (sonElMismoNumero(documentNumber, taxId)) {
        // Mismo número que ya se ve en el casillero (con o sin guiones, con o sin tipo
        // cargado) — no hay un SEGUNDO documento distinto que mostrar.
        return null;
    }

    // A partir de acá, documentNumber es un número REAL y DISTINTO del taxId vigente:
    // hay un documento guardado que el casillero no muestra. `documentType` puede venir
    // con un tipo concreto (CUIT/CUIL/DNI/Pasaporte/Otro) o en `null` (caso JAIR) — en
    // los dos casos hay que mostrarlo, la única diferencia es la etiqueta (ver
    // describirDocumentoAlternativo).
    return { tipoDocumento: documentType, numeroDocumento: documentNumber };
}

/**
 * Arma el texto legible del documento alternativo para la línea "También tiene ..." de
 * la ficha del cliente (fix del reviewer, 2026-07-27): mostrar el tipo tal cual funciona
 * bien para DNI/Pasaporte/CUIT/CUIL ("También tiene DNI 36.053.656"), pero para el tipo
 * "Otro" ("También tiene Otro AB123") suena como si "Otro" fuera el NOMBRE del
 * documento en vez de una categoría genérica — se reemplaza por una frase legible
 * ("también tiene otro documento AB123").
 *
 * Fix bloqueante del reviewer (2026-07-27, caso real "JAIR" verificado en PROD):
 * `tipoDocumento` puede venir en `null` (dato legacy sin tipo cargado en la BD, solo hay
 * número) — para ese caso se arma una frase genérica ("también tiene un documento
 * 36053656"), coherente con el mismo criterio que "Otro": nunca se inventa un tipo que
 * no está guardado, pero tampoco se esconde el número.
 *
 * @param {{tipoDocumento: string|null, numeroDocumento: string}|null|undefined} documentoAlternativo
 * @returns {string} texto listo para insertar después de "También tiene " (cadena vacía
 *   si no hay documento alternativo, para que el caller pueda usarlo sin chequear null).
 */
export function describirDocumentoAlternativo(documentoAlternativo) {
    if (!documentoAlternativo) {
        return "";
    }
    if (!documentoAlternativo.tipoDocumento) {
        // Caso JAIR: no hay tipo guardado, pero el número es un documento real.
        return `un documento ${documentoAlternativo.numeroDocumento}`;
    }
    if (documentoAlternativo.tipoDocumento === "Otro") {
        return `otro documento ${documentoAlternativo.numeroDocumento}`;
    }
    return `${documentoAlternativo.tipoDocumento} ${documentoAlternativo.numeroDocumento}`;
}

/**
 * Aplica un resultado elegido del padrón AFIP al casillero único (hallazgo B1, revisión
 * 2026-07-27): `persona.id` que devuelve `/fiscal/search` SIEMPRE es el CUIT/CUIL de 11
 * dígitos del padrón — el motor calcula los CUILes candidatos a partir de un DNI para
 * poder buscarlo ahí (nunca devuelve un DNI de 7-8 dígitos como "id"). Si el casillero
 * estaba en un tipo NO fiscal (por ejemplo DNI) y solo pisábamos el número, quedaba
 * "DNI" con un número de 11 dígitos y sin `taxId` — una identidad fiscal falsa a la
 * hora de facturar. Por eso, si el tipo actual no es fiscal, se lo sube a CUIT (lo que
 * vino del padrón ES un CUIT/CUIL); si ya era CUIT o CUIL, se respeta tal cual.
 *
 * @param {{tipoDocumento: string, numeroDocumento: string}} casilleroActual
 * @param {{id?: string}} persona - resultado elegido de `/fiscal/search`
 * @returns {{tipoDocumento: string, numeroDocumento: string}}
 */
export function aplicarResultadoAfip(casilleroActual, persona) {
    const tipoDocumento = esTipoDocumentoFiscal(casilleroActual?.tipoDocumento)
        ? casilleroActual.tipoDocumento
        : "CUIT";
    return {
        tipoDocumento,
        numeroDocumento: persona?.id || casilleroActual?.numeroDocumento || "",
    };
}
