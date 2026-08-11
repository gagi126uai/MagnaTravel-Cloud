import { useEffect, useState } from "react";
import { parseArgentineMoneyInput } from "../../lib/utils";

/**
 * Campo de texto para cargar un monto de plata en formato argentino.
 *
 * Por qué NO es <input type="number">: los inputs numéricos nativos del navegador
 * usan SIEMPRE punto como separador decimal — si el vendedor tipea "250,50" (como
 * factura, como habla en la agencia) el navegador descarta la coma y el campo queda
 * VACÍO. Bug real en PROD (QA 11/08/2026). Este componente es un <input type="text">
 * que entiende los formatos que un vendedor puede tipear: "250,50" (coma decimal, el
 * uso normal), "1.250,50" (miles + decimal) y también "250.50" (con punto, por si
 * pega un valor copiado) — ver parseArgentineMoneyInput() en lib/utils.js.
 *
 * Contrato con el formulario que lo usa (para no tener que tocar el resto del código):
 * `value` es lo que hoy guarda el form (una string numérica, ej. "250.5") y `onChange`
 * devuelve ese MISMO tipo de string (siempre con punto decimal, lista para Number(...)).
 * Lo único que cambia es que ahora el vendedor puede tipear con coma.
 */
export function MoneyInput({
    id,
    value,
    onChange,
    placeholder = "0,00",
    className,
    required,
    disabled,
    "data-testid": dataTestId,
    "aria-label": ariaLabel,
}) {
    const [texto, setTexto] = useState(() => textoParaMostrar(value));

    // Sincronizamos el texto visible con `value` SOLO cuando el cambio vino de AFUERA
    // (ej: el vendedor eligió un producto del buscador y se precargó un precio
    // sugerido). Si el cambio es el eco de nuestro propio onChange (mismo número, en
    // otro formato), no lo tocamos — si no, le comeríamos la coma mientras el vendedor
    // todavía está escribiendo (ej: "250," se convertiría en "250" apenas el padre nos
    // devuelve el valor en el próximo render).
    useEffect(() => {
        const numeroEntrante = value === "" || value === null || value === undefined ? null : Number(value);
        const numeroActual = parseArgentineMoneyInput(texto);
        if (numeroEntrante === numeroActual) return;
        setTexto(textoParaMostrar(value));
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [value]);

    const handleChange = (event) => {
        // Filtro liviano mientras tipea: solo dígitos, coma y punto — nada de letras
        // ni signo menos (un monto de plata acá nunca es negativo).
        const crudo = event.target.value.replace(/[^\d.,]/g, "");
        setTexto(crudo);
        const numero = parseArgentineMoneyInput(crudo);
        onChange(numero === null ? "" : String(numero));
    };

    // Fix I4 (review): mientras el vendedor tipea, el texto en pantalla puede quedar
    // en un estado que NO parsea a ningún número ("1.250.50", dos puntos) — en ese
    // caso `handleChange` ya le mandó "" al form (arriba), pero el campo seguía
    // MOSTRANDO "1.250.50", como si tuviera un precio cargado. Al perder el foco
    // normalizamos para que pantalla y form dejen de contradecirse:
    //   - si lo tipeado SÍ se entiende, mostramos la versión ya interpretada (prolija,
    //     con coma decimal) — así el vendedor ve EXACTO lo que se va a guardar;
    //   - si NO se entiende nada, mostramos lo que el form tiene de verdad (`value`) —
    //     que a esta altura ya es "" porque `handleChange` lo vació con esa misma
    //     tecleada: el campo queda VACÍO, igual que el form. Nunca dejamos en pantalla
    //     un texto que no se corresponda con lo que en los hechos quedó guardado.
    const handleBlur = () => {
        const numero = parseArgentineMoneyInput(texto);
        setTexto(numero === null ? textoParaMostrar(value) : textoParaMostrar(String(numero)));
    };

    return (
        <input
            id={id}
            type="text"
            inputMode="decimal"
            className={className}
            value={texto}
            onChange={handleChange}
            onBlur={handleBlur}
            placeholder={placeholder}
            required={required}
            disabled={disabled}
            data-testid={dataTestId}
            aria-label={ariaLabel}
        />
    );
}

function textoParaMostrar(value) {
    if (value === "" || value === null || value === undefined) return "";
    // El form guarda el valor "canónico" (punto decimal, ej. "1250.5") — lo mostramos
    // con coma, que es lo que el vendedor espera ver en un campo de plata argentino.
    return String(value).replace(".", ",");
}
