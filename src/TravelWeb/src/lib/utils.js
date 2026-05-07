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
