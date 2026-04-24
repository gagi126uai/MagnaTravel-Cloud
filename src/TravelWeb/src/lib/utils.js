import { clsx } from "clsx";
import { twMerge } from "tailwind-merge";

export function cn(...inputs) {
    return twMerge(clsx(inputs));
}

export function formatCurrency(amount) {
    if (amount === undefined || amount === null) return "$0.00";
    return new Intl.NumberFormat("en-US", {
        style: "currency",
        currency: "USD",
        minimumFractionDigits: 2,
    }).format(amount);
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
 * Deeply converts object keys to camelCase to ensure consistency
 * regardless of backend naming policy (PascalCase vs camelCase).
 */
export function camelize(obj) {
    if (obj === null || obj === undefined) return obj;
    
    if (Array.isArray(obj)) {
        return obj.map(v => camelize(v));
    } else if (typeof obj === 'object' && obj.constructor === Object) {
        return Object.keys(obj).reduce((result, key) => {
            const camelKey = key.charAt(0).toLowerCase() + key.slice(1);
            result[camelKey] = camelize(obj[key]);
            return result;
        }, {});
    }
    return obj;
}
