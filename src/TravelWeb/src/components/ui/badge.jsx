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

export { Badge, badgeVariants }
