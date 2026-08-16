import * as React from "react"
import { cn } from "../../lib/utils"

const badgeVariants = (variant, className) => {
    const base = "inline-flex items-center rounded-full border px-2.5 py-0.5 text-xs font-semibold transition-colors focus:outline-none focus:ring-2 focus:ring-ring focus:ring-offset-2"

    let vClass = ""
    switch (variant) {
        case "secondary": vClass = "border-transparent bg-secondary text-secondary-foreground hover:bg-secondary/80"; break;
        case "destructive": vClass = "border-transparent bg-destructive text-destructive-foreground hover:bg-destructive/80"; break;
        case "outline": vClass = "text-foreground"; break;
        case "success": vClass = "border-transparent bg-emerald-100 text-emerald-700 hover:bg-emerald-200"; break;
        default: vClass = "border-transparent bg-primary text-primary-foreground hover:bg-primary/80"; break; // default
    }

    return cn(base, vClass, className)
}

function Badge({ className, variant = "default", ...props }) {
    return (
        <div className={badgeVariants(variant, className)} {...props} />
    )
}

/**
 * StatusChip — molde de chip de estado del estandar visual (B.5, Lavado de cara,
 * docs/ux/2026-08-11-estandar-visual-y-lavado-de-cara.md). Tanda D0 (2026-08-16):
 * se agrega ACA, sin tocar `Badge`/`badgeVariants` de arriba, porque ese `Badge`
 * generico ya lo usan 25 pantallas con su look actual (Reportes, Caja, Proveedores,
 * Clientes...) y esta tanda es SOLO el molde nuevo, cero barrido de pantallas.
 *
 * Uso: para todo chip de estado NUEVO en las tandas D1 en adelante (Clientes,
 * Cobranzas, Caja, Proveedores, Tarifario, CRM). NO reemplaza a `ReservaStatusBadge`
 * (esa pieza de Reservas ya cumple el molde por su cuenta, no se toca).
 *
 * Medida fija del estandar, sin excepciones: 24px de alto, redondo completo, letra
 * 11px mayuscula, borde de 1px del mismo tono que el texto, fondo palido. Un chip
 * NUNCA lleva emoji — si hace falta un icono, va un icono de lucide-react antes del
 * texto.
 *
 * `tone` es el UNICO eje de color y siempre tiene que significar algo (P-20 de la
 * constitucion: "un color, un significado"), nunca es decorativo:
 *   - "neutro" -> gris:  estado sin urgencia (ej. "Borrador", "Inactivo")
 *   - "azul"   -> en curso / informativo (ej. "En gestion", "Pendiente de envio")
 *   - "ambar"  -> te pide algo (ej. "Requiere revision", una sugerencia accionable)
 *   - "verde"  -> listo / entro plata (ej. "Saldado", "Confirmado")
 *   - "rojo"   -> freno / sin efecto (ej. "Anulado", "Rechazado")
 */
const STATUS_CHIP_TONES = {
    neutro: "border-slate-300 bg-slate-50 text-slate-600 dark:border-slate-700 dark:bg-slate-800/60 dark:text-slate-400",
    azul: "border-blue-300 bg-blue-50 text-blue-700 dark:border-blue-800 dark:bg-blue-900/20 dark:text-blue-300",
    ambar: "border-amber-300 bg-amber-50 text-amber-800 dark:border-amber-800 dark:bg-amber-950/40 dark:text-amber-300",
    verde: "border-emerald-300 bg-emerald-50 text-emerald-700 dark:border-emerald-800 dark:bg-emerald-900/20 dark:text-emerald-300",
    rojo: "border-rose-300 bg-rose-50 text-rose-700 dark:border-rose-800 dark:bg-rose-900/20 dark:text-rose-300",
}

function StatusChip({ tone = "neutro", className, children, ...props }) {
    const toneClass = STATUS_CHIP_TONES[tone] ?? STATUS_CHIP_TONES.neutro
    return (
        <span
            className={cn(
                "inline-flex h-6 items-center gap-1 whitespace-nowrap rounded-full border px-2.5 text-[11px] font-semibold uppercase tracking-wide",
                toneClass,
                className
            )}
            {...props}
        >
            {children}
        </span>
    )
}

export { Badge, badgeVariants, StatusChip }
