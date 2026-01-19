import * as React from "react"
import { cn } from "../../lib/utils"

const buttonVariants = (variant, size, className) => {
    const base = "inline-flex items-center justify-center rounded-md text-sm font-medium ring-offset-background transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:pointer-events-none disabled:opacity-50"

    let vClass = ""
    switch (variant) {
        case "destructive": vClass = "bg-destructive text-destructive-foreground hover:bg-destructive/90"; break;
        case "outline": vClass = "border border-input bg-background hover:bg-accent hover:text-accent-foreground"; break;
        case "secondary": vClass = "bg-secondary text-secondary-foreground hover:bg-secondary/80"; break;
        case "ghost": vClass = "hover:bg-accent hover:text-accent-foreground"; break;
        case "link": vClass = "text-primary underline-offset-4 hover:underline"; break;
        default: vClass = "bg-primary text-primary-foreground hover:bg-primary/90"; break; // default
    }

    let sClass = ""
    switch (size) {
        case "sm": sClass = "h-9 rounded-md px-3"; break;
        case "lg": sClass = "h-11 rounded-md px-8"; break;
        case "icon": sClass = "h-10 w-10"; break;
        default: sClass = "h-10 px-4 py-2"; break; // default
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
