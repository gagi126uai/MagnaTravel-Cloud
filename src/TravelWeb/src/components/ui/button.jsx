import * as React from "react"
import { cn } from "../../lib/utils"

/**
 * Sistema de botones de MagnaTravel — Lavado de cara, estandar visual firmado 2026-08-11
 * (docs/ux/2026-08-11-estandar-visual-y-lavado-de-cara.md, seccion B.3).
 *
 * La idea central que el dueno pidio arreglar: "el boton que NO querias que usen
 * (Perdida/Archivar) se veia mas grande que el boton importante (El cliente acepto)".
 * La solucion es tener SOLO estos 5 niveles, siempre con la MISMA altura por contexto
 * (40px normal, 32px "chico" dentro de una fila de tabla), para que nunca mas un boton
 * secundario le gane en tamano/peso visual al principal:
 *
 *   1. "default"     -> PRINCIPAL. Relleno de azul boleto (el UNICO color de accion de
 *                       toda la app, ver --primary en styles.css). Va UNA sola vez por
 *                       pantalla: la accion que el vendedor vino a hacer.
 *   2. "outline"      -> SECUNDARIA. Fondo blanco, borde gris fino, letra tinta. Acciones
 *                       normales que no son "la" accion (ej. "Limpiar busqueda").
 *   3. "ghost"        -> TERCIARIA (fantasma). Sin fondo ni borde, letra gris — salidas
 *                       laterales que casi no se usan (ej. iconos de la barra superior).
 *   4. "destructive"  -> DESTRUCTIVA DISCRETA. Letra roja + borde rosado, NUNCA relleno
 *                       de rojo (anular/borrar siempre piden confirmacion aparte, P-14).
 *   5. (nativo)        -> APAGADA. El atributo `disabled` ya trae opacidad + cursor
 *                       bloqueado (ver `disabled:` en `base` mas abajo). El motivo del
 *                       bloqueo (P-9) es responsabilidad de quien usa el boton, no de
 *                       este componente — normalmente va en un `title` sobre un <span>
 *                       que envuelve al boton (un boton disabled no dispara hover).
 */
const buttonVariants = (variant, size, className) => {
    const base = "inline-flex items-center justify-center rounded-[10px] text-sm font-medium ring-offset-background transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:pointer-events-none disabled:opacity-50"

    let vClass = ""
    switch (variant) {
        // Nivel 4 (destructiva discreta): antes era rojo RELLENO — el estandar 2026-08-11
        // lo prohibe explicitamente ("nunca relleno de rojo"). Mismo molde que "outline"
        // (fondo blanco, borde), pero en rosado/rojo para que se lea "cuidado" sin gritar.
        case "destructive": vClass = "border border-rose-200 bg-white text-rose-700 hover:bg-rose-50 hover:border-rose-300 dark:border-rose-900 dark:bg-transparent dark:text-rose-300 dark:hover:bg-rose-950/30"; break;
        case "outline": vClass = "border border-input bg-background hover:bg-accent hover:text-accent-foreground"; break;
        case "secondary": vClass = "bg-secondary text-secondary-foreground hover:bg-secondary/80"; break;
        // Nivel 3 (terciaria/fantasma): letra gris dato por defecto (antes heredaba el
        // color de texto normal, tan oscuro como el resto — por eso competia visualmente
        // con las acciones de verdad). El fondo solo aparece al pasar el mouse.
        case "ghost": vClass = "text-muted-foreground hover:bg-accent hover:text-accent-foreground"; break;
        case "link": vClass = "text-primary underline-offset-4 hover:underline"; break;
        default: vClass = "bg-primary text-primary-foreground hover:bg-primary/90"; break; // default = Nivel 1, principal
    }

    let sClass = ""
    switch (size) {
        // "sm" = el boton "chico" que pide el estandar para vivir DENTRO de una fila de
        // tabla (antes 36px, ahora 32px — h-8 de Tailwind es exactamente eso).
        case "sm": sClass = "h-8 rounded-[10px] px-3"; break;
        case "lg": sClass = "h-11 rounded-[10px] px-8"; break;
        case "icon": sClass = "h-10 w-10"; break;
        default: sClass = "h-10 px-4 py-2"; break; // default = 40px, el alto "normal" del estandar
    }

    return cn(base, vClass, sClass, className)
}

const Button = React.forwardRef(({ className, variant = "default", size = "default", asChild = false, ...props }, ref) => {
    return (
        <button
            className={buttonVariants(variant, size, className)}
            ref={ref}
            {...props}
        />
    )
})
Button.displayName = "Button"

export { Button, buttonVariants }
