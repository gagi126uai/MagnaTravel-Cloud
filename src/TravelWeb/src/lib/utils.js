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

export function formatDate(date) {
    if (!date) return "-";
    return new Date(date).toLocaleDateString("es-AR", {
        day: "2-digit",
        month: "2-digit",
        year: "numeric"
    });
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
