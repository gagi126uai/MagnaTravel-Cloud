import { clsx } from "clsx";
import { twMerge } from "tailwind-merge";

export function cn(...inputs) {
    return twMerge(clsx(inputs));
}

/**
 * Formatea un monto con el símbolo y locale de la moneda indicada.
 *
 * REGLA B-1 (fix 2026-06-11): el default cuando NO se pasa currency es USD/en-US,
 * igual que HEAD (antes de la tanda multimoneda). Todos los call sites que ya existían
 * —y no pasan currency— siguen recibiendo el mismo formato que antes.
 * Los call sites nuevos de multimoneda SIEMPRE pasan currency explícita.
 *
 * Regla del contador (2026-06-09): pesos y dólares siempre separados, nunca sumados.
 * El símbolo "US$" (no "$") distingue a ojo el dólar del peso en la pantalla.
 *
 * @param {number|string|null|undefined} amount
 * @param {"ARS"|"USD"|undefined} currency - Default: comportamiento legacy USD/en-US
 */
export function formatCurrency(amount, currency) {
    if (amount === undefined || amount === null) {
        // Default legacy: misma cadena que HEAD para null/undefined sin currency
        if (!currency) return "$0.00";
        return currency === "USD" ? "US$0.00" : "$0,00";
    }
    const number = Number(amount);

    if (currency === "ARS") {
        // ARS explícito: peso argentino, formato es-AR
        return new Intl.NumberFormat("es-AR", {
            style: "currency",
            currency: "ARS",
            minimumFractionDigits: 2,
        }).format(number);
    }

    if (currency === "USD") {
        // USD explícito: usamos "US$" para distinguirlo del peso en pantalla.
        return "US$" + new Intl.NumberFormat("es-AR", {
            minimumFractionDigits: 2,
            maximumFractionDigits: 2,
        }).format(number);
    }

    // Sin currency (o currency desconocido): comportamiento legacy idéntico a HEAD (USD/en-US)
    return new Intl.NumberFormat("en-US", {
        style: "currency",
        currency: "USD",
        minimumFractionDigits: 2,
    }).format(number);
}

// Bug "fechas corridas un día" (reportado 2026-07-16): reconoce una fecha que en
// realidad es "un día calendario" (no un instante real con hora). Dos formas posibles:
//   1. "2026-05-23"                      → value crudo de un <input type="date">
//   2. "2026-05-23T00:00:00[.000][Z]"    → el backend guarda esas fechas como
//      medianoche UTC (columnas timestamp with time zone que no tienen hora real).
// En ambos casos el "23" es el día que el usuario eligió — NO hay que convertirlo a
// hora local, porque en Argentina (UTC-3) la medianoche UTC del día 23 cae a las
// 21:00 del día 22, y new Date(...).toLocaleDateString() mostraría "22/05/2026".
const FECHA_SOLO_DIA_REGEX = /^(\d{4})-(\d{2})-(\d{2})(?:T00:00:00(?:\.\d+)?Z?)?$/;

/**
 * Formatea una fecha para mostrarla al usuario como "DD/MM/AAAA".
 *
 * Discrimina dos casos (ver FECHA_SOLO_DIA_REGEX arriba):
 *   - Fecha-solo-día (input date, o medianoche UTC guardada por el backend):
 *     se lee el día/mes/año directo del texto (string-split), sin pasar por
 *     new Date(), para no correr el día por la zona horaria del navegador.
 *   - Cualquier otro instante (ej. createdAt de una factura, con hora real):
 *     se muestra en la fecha que corresponde en Argentina (America/Argentina/Buenos_Aires),
 *     fijada EXPLÍCITAMENTE — no la que el navegador/servidor tengan configurada.
 *
 * Regla del dueño (2026-07-22): la fecha que rige es SIEMPRE la de Argentina, sin importar
 * dónde esté el servidor o el navegador. Por eso NO alcanza con pasar el locale "es-AR" a
 * Intl (el locale solo define el ORDEN día/mes/año, no la zona horaria de la conversión) —
 * hay que fijar `timeZone: "America/Argentina/Buenos_Aires"` explícito en las opciones.
 *
 * Nota: si por coincidencia un evento con hora real ocurrió EXACTO a la
 * medianoche UTC, esta función lo trata igual que una fecha-solo-día (mismo
 * día calendario, sin desfase). Es una ambigüedad inherente al formato — no
 * hay forma de distinguir ambos casos solo mirando el string.
 *
 * Fix 2026-07-23 (hallazgo del gate data-exposure): un string que no es una fecha válida
 * (dato sucio, o un bug en otro lado que manda basura) ANTES caía en `new Date(...)`, que
 * para un string no parseable da un objeto "Invalid Date" — y `toLocaleDateString()` sobre
 * eso devuelve LITERALMENTE el texto en inglés "Invalid Date", que se mostraría tal cual en
 * una pantalla en castellano para un usuario no programador. Ahora se detecta ANTES de
 * formatear y se degrada a "-" (el mismo símbolo que usamos para "sin dato"), nunca un
 * mensaje técnico.
 */
export function formatDate(date) {
    if (!date) return "-";

    if (typeof date === "string") {
        const match = FECHA_SOLO_DIA_REGEX.exec(date);
        if (match) {
            const [, anio, mes, dia] = match;
            return `${dia}/${mes}/${anio}`;
        }
    }

    const parsed = new Date(date);
    if (Number.isNaN(parsed.getTime())) return "-";

    return parsed.toLocaleDateString("es-AR", {
        day: "2-digit",
        month: "2-digit",
        year: "numeric",
        timeZone: "America/Argentina/Buenos_Aires",
    });
}

/**
 * Formatea una fecha CON hora para mostrarla al usuario ("DD/MM/AAAA, HH:mm").
 *
 * Mismo bug que formatDate() de arriba, pero para las pantallas que además quieren
 * mostrar la hora (ej. Movimientos de caja / Historial de cobros). La diferencia es que
 * acá SÍ puede haber un instante con hora real que valga la pena mostrar en hora local
 * (ej. la reversa de un asiento de caja, que ocurre "ahora").
 *
 * Regla (2026-07-23, fix bug "cobro del 22/07 aparecía como 21/7" en el extracto — la
 * misma causa afecta a Movimientos/Historial): si la hora en UTC es EXACTAMENTE medianoche
 * (00:00:00), el valor es una FECHA DE NEGOCIO (día que el usuario eligió — ej. PaidAt de un
 * cobro/pago — que el backend guarda como medianoche UTC sin hora real), no un instante real.
 * En ese caso mostramos SOLO el día (formatDate central), sin inventar una hora que no existe:
 * convertir esa medianoche a hora de Argentina (UTC-3) la corre al día anterior a las 21:00,
 * que es exactamente el bug. Si la hora NO es medianoche, es un instante real (ej. "ahora" de
 * un ajuste manual) y ahí se muestra en la hora de Argentina, fijada explícita (mismo motivo
 * que formatDate(): el locale "es-AR" no define zona horaria, así que sin `timeZone` explícito
 * la conversión dependería de dónde esté el navegador/servidor — la regla del dueño exige que
 * la hora que se muestra sea SIEMPRE la de Argentina).
 *
 * Fix 2026-07-23 (hallazgo del gate data-exposure, mismo criterio que formatDate()): un valor
 * que no es una fecha válida se degrada a "-", nunca al texto técnico "Invalid Date".
 */
export function formatDateTime(date) {
    if (!date) return "-";

    const parsed = new Date(date);
    if (Number.isNaN(parsed.getTime())) return "-";

    const esFechaDeNegocioSinHoraReal =
        parsed.getUTCHours() === 0 && parsed.getUTCMinutes() === 0 && parsed.getUTCSeconds() === 0;

    if (esFechaDeNegocioSinHoraReal) {
        return formatDate(date);
    }

    return parsed.toLocaleString("es-AR", { timeZone: "America/Argentina/Buenos_Aires" });
}

/**
 * Derives up to 2 uppercase initials from a full name string.
 * Returns "?" when name is empty or not a string.
 */
export function getInitials(name) {
  if (!name || typeof name !== "string") return "?";
  return name
    .trim()
    .split(/\s+/)
    .map((part) => part[0])
    .join("")
    .substring(0, 2)
    .toUpperCase();
}

/**
 * Deeply converts object keys to camelCase to ensure consistency
 * regardless of backend naming policy (PascalCase vs camelCase).
 */
export function camelize(obj) {
    if (obj === null || obj === undefined) return obj;
    
    if (Array.isArray(obj)) {
        return obj.map(v => camelize(v));
    } else if (typeof obj === 'object') {
        // Handle dates or other objects that shouldn't be camelized recursively
        if (obj instanceof Date) return obj;
        
        return Object.keys(obj).reduce((result, key) => {
            const camelKey = key.charAt(0).toLowerCase() + key.slice(1);
            result[camelKey] = camelize(obj[key]);
            return result;
        }, {});
    }
    return obj;
}
