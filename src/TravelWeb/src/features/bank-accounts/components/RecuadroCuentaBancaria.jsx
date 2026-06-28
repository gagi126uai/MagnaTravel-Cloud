/**
 * Muestra la cuenta bancaria de cualquier dueño (agencia, cliente o proveedor)
 * con botón "Copiar CBU" / "Copiar alias", para facilitar transferencias sin
 * que el usuario tenga que tipear datos bancarios a mano.
 *
 * El dato completo (CBU/alias sin tapar) se obtiene llamando al endpoint de
 * detalle (GET /bank-accounts/{publicId}), que registra la lectura en auditoría.
 * La lista inicial llega con CBU/alias enmascarados (solo últimos dígitos).
 *
 * Si hay más de una cuenta en la moneda indicada, muestra un selector para
 * que el usuario elija cuál usar; de lo contrario, preselecciona la principal.
 *
 * Momentos de uso en el sistema:
 *   1. Cobro al cliente → cuenta de la AGENCIA en la moneda del cobro
 *      (el agente le dice al cliente a dónde tiene que transferir).
 *   2. Devolución a cliente → cuenta del CLIENTE en la moneda del saldo
 *      (el agente sabe a dónde tiene que transferirle la plata de vuelta).
 *
 * Comportamiento cuando no hay cuenta para la moneda:
 *   - mensajeSinCuenta = null (default): el recuadro no se muestra (silencioso).
 *   - mensajeSinCuenta = string: muestra ese texto como aviso suave en amarillo.
 *
 * Manejo de errores de carga:
 *   - 403 (sin permiso): degradar en silencio → return null. El cajero puede no tener
 *     el permiso configuracion.view y aun así debe poder registrar el cobro.
 *     Mostrar "Reintentar" sería un callejón sin salida porque el 403 no va a cambiar.
 *   - Otros errores (red, 500, 503): mostrar bloque rojo con "Reintentar", porque
 *     son transitorios y el siguiente intento podría tener éxito.
 *
 * Props:
 *   - ownerType: number         — enum del backend: 0=Agency, 1=Customer, 2=Supplier
 *   - ownerId: string|number    — 0 para Agency; publicId para Customer/Supplier
 *   - moneda: "ARS"|"USD"       — moneda en la que se busca la cuenta principal
 *   - titulo: string            — texto del encabezado del recuadro
 *   - mensajeSinCuenta: string|null — qué mostrar si no hay cuenta (null = ocultar)
 */

import { useState, useEffect, useCallback } from "react";
import { Banknote, Copy, Check, ChevronDown, AlertCircle } from "lucide-react";
import { api } from "../../../api";
import { getApiErrorMessage } from "../../../lib/errors";
import {
    ACCOUNT_TYPE,
    resolverCuentaPrincipalPorMoneda,
    clasificarErrorCuenta,
} from "../lib/bankAccountLogic";

// Etiquetas legibles para el tipo de cuenta bancaria (valor int del backend).
// IMPORTANTE: usar != null para no omitir el 0 (CajaAhorro = 0 es falsy en JS).
const LABEL_TIPO_CUENTA = {
    [ACCOUNT_TYPE.CajaAhorro]: "Caja de Ahorro",
    [ACCOUNT_TYPE.CuentaCorriente]: "Cuenta Corriente",
};

export function RecuadroCuentaBancaria({
    ownerType,
    ownerId,
    moneda,
    titulo,
    mensajeSinCuenta = null,
}) {
    const [cuentas, setCuentas] = useState([]);
    const [cargandoCuentas, setCargandoCuentas] = useState(true);

    // errorRecuperable: solo se seteacon errores transitorios (500, red, etc.).
    // Un 403 se trata por separado con sinPermiso=true → degrade silenciosa.
    const [errorRecuperable, setErrorRecuperable] = useState(null);
    const [sinPermiso, setSinPermiso] = useState(false);

    const [cuentaSeleccionada, setCuentaSeleccionada] = useState(null);

    // Estado del botón copiar: null | "copiando" | "copiado" | "error"
    const [estadoCopia, setEstadoCopia] = useState(null);

    // Función separada para poder reutilizarla desde el botón "Reintentar".
    // useCallback con [ownerType, ownerId]: solo se recrea si el dueño cambia.
    // La moneda no está acá: cambiar la moneda no recarga la lista, solo
    // recalcula cuál cuenta mostrar (lo hace el useEffect de [cuentas, moneda]).
    const cargarCuentas = useCallback(() => {
        // ownerId puede ser 0 (Agency): comparamos != null para no descartar el 0
        if (ownerType == null || ownerId == null) return;

        let cancelled = false;
        setCargandoCuentas(true);
        setErrorRecuperable(null);
        setSinPermiso(false);

        const params = new URLSearchParams({
            ownerType: String(ownerType),
            ownerId: String(ownerId),
        });

        api.get(`/bank-accounts?${params.toString()}`)
            .then((data) => {
                if (cancelled) return;
                const lista = Array.isArray(data) ? data : (data?.items ?? []);
                setCuentas(lista);
            })
            .catch((err) => {
                if (cancelled) return;
                const clasificacion = clasificarErrorCuenta(err);
                if (clasificacion === "sin_permiso") {
                    // 403: el usuario no tiene configuracion.view — degradar en silencio.
                    // No ponemos errorRecuperable para que la UI devuelva null sin ruido.
                    setSinPermiso(true);
                } else {
                    // Error transitorio: mostrar con opción de reintentar
                    setErrorRecuperable(
                        getApiErrorMessage(err, "No se pudieron cargar los datos bancarios.")
                    );
                }
            })
            .finally(() => {
                // N1: cuando cargarCuentas viene del botón "Reintentar" (no del useEffect),
                // `cancelled` nunca se pone a true si el componente se desmonta en vuelo.
                // En React 18 setState sobre un componente desmontado es un no-op silencioso,
                // por lo que no genera error ni pérdida de datos. Se acepta sin guard adicional.
                if (!cancelled) setCargandoCuentas(false);
            });

        return () => { cancelled = true; };
    }, [ownerType, ownerId]);

    // Carga las cuentas al montar o cuando cambia el dueño.
    // El useEffect recibe la función de limpieza que `cargarCuentas` retorna.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    useEffect(() => {
        const cleanup = cargarCuentas();
        return cleanup;
    }, [ownerType, ownerId]);

    // Cuando cambia la moneda del formulario padre, recalculamos qué cuenta mostrar.
    // No recargamos la lista: ya tenemos todas las cuentas del dueño en `cuentas`.
    // Dependencias: cuentas (lista ya cargada) y moneda (prop del padre).
    useEffect(() => {
        if (cuentas.length === 0) return;
        const principal = resolverCuentaPrincipalPorMoneda(cuentas, moneda);
        setCuentaSeleccionada(principal ?? null);
        setEstadoCopia(null);
    }, [cuentas, moneda]);

    const cuentasDeEstaMoneda = cuentas.filter(
        (c) => c.isActive !== false && c.currency === moneda
    );

    const handleCambiarCuenta = (publicId) => {
        const elegida = cuentas.find((c) => c.publicId === publicId);
        setCuentaSeleccionada(elegida ?? null);
        setEstadoCopia(null);
    };

    const handleCopiar = async () => {
        if (!cuentaSeleccionada) return;
        setEstadoCopia("copiando");

        try {
            // Endpoint de detalle: devuelve CBU/alias completo (sin tapar)
            // y registra la lectura en el log de auditoría del backend.
            const detalle = await api.get(`/bank-accounts/${cuentaSeleccionada.publicId}`);
            const textoCopiar = detalle?.cbu || detalle?.alias || "";
            if (!textoCopiar) {
                setEstadoCopia("error");
                return;
            }

            try {
                await navigator.clipboard.writeText(textoCopiar);
                setEstadoCopia("copiado");
            } catch {
                // Fallback para navegadores sin soporte de Clipboard API
                const textarea = document.createElement("textarea");
                textarea.value = textoCopiar;
                textarea.style.position = "fixed";
                textarea.style.opacity = "0";
                document.body.appendChild(textarea);
                textarea.focus();
                textarea.select();
                const exito = document.execCommand("copy");
                document.body.removeChild(textarea);
                setEstadoCopia(exito ? "copiado" : "error");
            }
        } catch (err) {
            console.warn("[RecuadroCuentaBancaria] Error al copiar:", err?.message);
            setEstadoCopia("error");
        }

        // Restablecer el estado del botón después de 2,5 segundos
        setTimeout(() => setEstadoCopia(null), 2500);
    };

    // ── Estados de render ─────────────────────────────────────────────────────

    // 403 sin permiso: degradar en silencio. El recuadro desaparece sin ruido.
    // Esto cubre el caso del cajero/vendedor que no tiene configuracion.view:
    // no debe ver un error rojo embebido en el formulario de cobro.
    if (sinPermiso) return null;

    // Error recuperable (red, 500, 503): mostrar con Reintentar
    if (!cargandoCuentas && errorRecuperable) {
        return (
            <div
                className="rounded-xl border border-rose-200 bg-rose-50 dark:border-rose-900/40 dark:bg-rose-950/10 p-4 space-y-2"
                data-testid="recuadro-cuenta-bancaria-error"
                role="alert"
            >
                <div className="flex items-center gap-2 text-sm font-semibold text-rose-700 dark:text-rose-300">
                    <AlertCircle className="h-4 w-4 flex-shrink-0" />
                    No se pudieron cargar los datos bancarios.
                </div>
                {/* Reintentar: ver nota N1 en el finally de cargarCuentas sobre React 18 */}
                <button
                    type="button"
                    onClick={cargarCuentas}
                    className="rounded-lg border border-rose-200 bg-white px-3 py-1.5 text-xs font-medium text-rose-700 hover:bg-rose-50 dark:border-rose-900/40 dark:bg-slate-900 dark:text-rose-400 transition-colors"
                >
                    Reintentar
                </button>
            </div>
        );
    }

    // Sin cuentas para esta moneda:
    //   - mensajeSinCuenta=null → ocultar (no bloquear el flujo del formulario)
    //   - mensajeSinCuenta=string → aviso suave en amarillo
    if (!cargandoCuentas && cuentasDeEstaMoneda.length === 0) {
        if (!mensajeSinCuenta) return null;
        return (
            <div
                className="rounded-xl border border-amber-200 bg-amber-50 dark:border-amber-900/40 dark:bg-amber-950/10 p-3"
                data-testid="recuadro-cuenta-bancaria-sin-cuenta"
            >
                <p className="text-xs text-amber-700 dark:text-amber-400">{mensajeSinCuenta}</p>
            </div>
        );
    }

    // Etiqueta visible del botón copiar
    const textoCopiarBoton =
        estadoCopia === "copiado"    ? "Copiado ✓" :
        estadoCopia === "error"      ? "Error al copiar" :
        estadoCopia === "copiando"   ? "Copiando…" :
        cuentaSeleccionada?.cbuMasked   ? "Copiar CBU" :
        cuentaSeleccionada?.aliasMasked ? "Copiar alias" :
        "Copiar";

    return (
        <div
            className="rounded-xl border border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/30 p-4 space-y-3"
            data-testid="recuadro-cuenta-bancaria"
        >
            {/* Encabezado del recuadro */}
            <div className="flex items-center gap-2">
                <Banknote className="h-4 w-4 text-slate-500" />
                <span className="text-sm font-semibold text-slate-700 dark:text-slate-300">
                    {titulo}
                </span>
                <span className="text-xs text-slate-400">
                    ({moneda === "USD" ? "US$" : "$"})
                </span>
            </div>

            {cargandoCuentas ? (
                <p className="text-xs text-slate-400 italic">Cargando datos bancarios…</p>
            ) : cuentaSeleccionada ? (
                <div className="space-y-2">
                    {/* Datos resumidos de la cuenta seleccionada */}
                    <div className="text-sm text-slate-800 dark:text-slate-200 space-y-0.5">
                        <p className="font-semibold">{cuentaSeleccionada.holderName}</p>
                        <p className="font-mono text-xs text-slate-500 dark:text-slate-400">
                            {cuentaSeleccionada.cbuMasked
                                ? "CBU " + cuentaSeleccionada.cbuMasked
                                : cuentaSeleccionada.aliasMasked
                                ? "alias " + cuentaSeleccionada.aliasMasked
                                : "Sin CBU ni alias guardado"
                            }
                        </p>
                        {cuentaSeleccionada.bank && (
                            // accountType es int (0=CajaAhorro, 1=CuentaCorriente).
                            // Usamos != null para no omitir el 0 (falsy en JS pero valor válido).
                            <p className="text-xs text-slate-400">
                                {cuentaSeleccionada.bank}
                                {cuentaSeleccionada.accountType != null
                                    ? ` — ${LABEL_TIPO_CUENTA[cuentaSeleccionada.accountType] ?? ""}`
                                    : ""}
                            </p>
                        )}
                    </div>

                    {/* Fila de acciones: cambiar cuenta (si hay varias) + copiar */}
                    <div className="flex flex-wrap items-center gap-2">
                        {/* Selector visible solo cuando el dueño tiene más de una cuenta en esta moneda */}
                        {cuentasDeEstaMoneda.length > 1 && (
                            <div className="relative">
                                <select
                                    value={cuentaSeleccionada.publicId}
                                    onChange={(e) => handleCambiarCuenta(e.target.value)}
                                    className="appearance-none rounded-lg border border-slate-200 bg-white pr-7 pl-3 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300 outline-none focus:ring-2 focus:ring-indigo-400"
                                    aria-label="Cambiar cuenta bancaria"
                                    data-testid="recuadro-selector-cuenta"
                                >
                                    {cuentasDeEstaMoneda.map((c) => (
                                        <option key={c.publicId} value={c.publicId}>
                                            {c.holderName}{c.isPrimary ? " ⭐" : ""}
                                            {c.bank ? ` — ${c.bank}` : ""}
                                            {" ("}{c.cbuMasked || c.aliasMasked || "sin CBU/alias"}{")"}
                                        </option>
                                    ))}
                                </select>
                                <ChevronDown className="pointer-events-none absolute right-2 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-slate-400" />
                            </div>
                        )}

                        {/* U2 (a11y): región aria-live que anuncia el resultado de la copia
                            al lector de pantalla. Se usa sr-only para ocultarla visualmente:
                            el texto visible ya está en el botón, pero el lector necesita
                            este nodo separado porque aria-live no funciona bien dentro de
                            elementos con role=button según WCAG 4.1.3. */}
                        <span
                            role="status"
                            aria-live="polite"
                            className="sr-only"
                        >
                            {estadoCopia === "copiado"
                                ? "CBU copiado al portapapeles"
                                : estadoCopia === "error"
                                ? "Error al copiar al portapapeles"
                                : ""}
                        </span>

                        {/* Botón copiar: llama al endpoint de detalle para obtener el dato completo */}
                        <button
                            type="button"
                            onClick={handleCopiar}
                            disabled={
                                estadoCopia === "copiando" ||
                                (!cuentaSeleccionada?.cbuMasked && !cuentaSeleccionada?.aliasMasked)
                            }
                            className={`inline-flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-xs font-semibold transition-colors disabled:opacity-50 ${
                                estadoCopia === "copiado"
                                    ? "border-emerald-200 bg-emerald-50 text-emerald-700 dark:border-emerald-900/40 dark:bg-emerald-950/20 dark:text-emerald-400"
                                    : estadoCopia === "error"
                                    ? "border-rose-200 bg-rose-50 text-rose-700 dark:border-rose-900/40 dark:text-rose-400"
                                    : "border-slate-200 bg-white text-slate-600 hover:bg-indigo-50 hover:text-indigo-700 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300"
                            }`}
                            data-testid="btn-copiar-cuenta-bancaria"
                        >
                            {estadoCopia === "copiado"
                                ? <Check className="h-3.5 w-3.5" />
                                : <Copy className="h-3.5 w-3.5" />
                            }
                            {textoCopiarBoton}
                        </button>
                    </div>
                </div>
            ) : (
                // Lista cargada pero sin cuenta que coincida con la moneda (estado transitorio)
                <p className="text-xs text-slate-400 italic">
                    No hay cuenta cargada en {moneda === "USD" ? "dólares" : "pesos"}.
                </p>
            )}
        </div>
    );
}
