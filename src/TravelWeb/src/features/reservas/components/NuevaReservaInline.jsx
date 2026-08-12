import { useEffect, useRef, useState } from "react";
import { Search } from "lucide-react";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { useDebounce } from "../../../hooks/useDebounce";
import { CustomerFormModal } from "../../customers/components/CustomerFormModal";

// Umbral y cantidad de resultados: mismo criterio que el buscador de productos
// del tarifario (ProductSearchField) para que el buscador de clientes se sienta
// igual en todo el producto.
const MIN_QUERY_LENGTH = 2;
const DEBOUNCE_MS = 300;
const MAX_DISPLAY_RESULTS = 6;

/**
 * Fila "Nueva reserva" que se despliega arriba del listado de Reservas.
 *
 * P6 (Tanda 3 del rediseño de Reservas, 2026-08-03, maqueta firmada sección 3):
 * reemplaza al viejo CreateReservaModal para el alta desde el listado. Antes el
 * modal tenía DOS controles para elegir un solo cliente (un buscador que solo
 * filtraba + un <select> aparte que era el que elegía de verdad). Acá hay un
 * único buscador con sugerencias mientras se escribe — mismo patrón que ya usa
 * el buscador de productos del tarifario — y, si el cliente no existe todavía,
 * la última fila de la lista abre el alta rápida de cliente que ya usa la
 * pantalla de Clientes (CustomerFormModal): no se repite ese formulario acá.
 *
 * P1 sigue siendo A (ADR-048, corregido por el dueño): la reserva NACE como
 * PRESUPUESTO, como hoy. Desempate del dueño (2026-08-11, decisión B1 — pisa el
 * intento previo de esa misma tarde de renombrar todo a "reserva"): el título de
 * la fila y el botón de enviar dicen "presupuesto" (no "reserva"), igual que el
 * botón "Nuevo Presupuesto" que abre esta fila — un solo nombre para toda la
 * etapa, no dos conviviendo. La palabra "Reserva" recién aparece cuando el
 * cliente acepta (eso es de la Tanda 2 del lavado de cara, no se toca acá). El
 * POST /reservas y la navegación posterior a la ficha son EXACTAMENTE los
 * mismos que usaba el modal viejo, no se tocó el motor.
 *
 * Props:
 *   clienteInicial — { publicId, fullName, ... } precargado cuando se llega acá
 *                     con ?create=1&customerPublicId=... (ej. desde la ficha de
 *                     un cliente). null → arranca vacío.
 *   onCreada(publicId) — la reserva se creó: el padre navega a la ficha nueva.
 *   onCancelar()        — el vendedor cerró la fila sin crear nada.
 */
export function NuevaReservaInline({ clienteInicial = null, onCreada, onCancelar }) {
    const [busqueda, setBusqueda] = useState(clienteInicial?.fullName || "");
    const [clienteSeleccionado, setClienteSeleccionado] = useState(clienteInicial);
    const [resultados, setResultados] = useState([]);
    const [buscando, setBuscando] = useState(false);
    // Fix bloqueante (review frontend, 2026-08-04): si la búsqueda de clientes falla
    // (red caída, 403 por permisos, etc.) hay que decirlo — antes se mostraba el
    // mismo texto que "no encontramos a nadie", disfrazando un error de un vacío
    // real. errorBusqueda guarda el mensaje amable a mostrar; null = sin error.
    const [errorBusqueda, setErrorBusqueda] = useState(null);
    const [mostrarDropdown, setMostrarDropdown] = useState(false);
    const [startDate, setStartDate] = useState("");
    const [creando, setCreando] = useState(false);
    const [modalClienteNuevoAbierto, setModalClienteNuevoAbierto] = useState(false);

    // -1 = ningún resultado resaltado por teclado. 0..resultados.length-1 = un
    // cliente. resultados.length = la fila "Es un cliente nuevo: crearlo acá".
    const [keyboardIndex, setKeyboardIndex] = useState(-1);

    // Con clienteInicial precargado (llegada desde ?create=1&customerPublicId=)
    // el input ya trae texto al montar, pero el usuario todavía no tocó nada:
    // este ref evita que el useEffect de búsqueda dispare solo al abrir la fila.
    const usuarioEscribiendo = useRef(!clienteInicial);
    const blurTimer = useRef(null);
    const listboxId = useRef(`nueva-reserva-listbox-${Math.random().toString(36).slice(2)}`);

    const debouncedBusqueda = useDebounce(busqueda, DEBOUNCE_MS);

    // Busca clientes en el motor apenas el usuario deja de tipear (debounce).
    // No corre en el montaje con clienteInicial precargado (ver usuarioEscribiendo).
    useEffect(() => {
        if (!usuarioEscribiendo.current) return undefined;

        const texto = debouncedBusqueda.trim();
        if (texto.length < MIN_QUERY_LENGTH) {
            setResultados([]);
            setErrorBusqueda(null);
            setMostrarDropdown(false);
            setKeyboardIndex(-1);
            return undefined;
        }

        let cancelado = false;
        (async () => {
            setBuscando(true);
            try {
                const params = new URLSearchParams({ search: texto, page: "1", pageSize: "25" });
                const data = await api.get(`/customers?${params.toString()}`);
                if (cancelado) return;
                setResultados((data?.items || []).slice(0, MAX_DISPLAY_RESULTS));
                setErrorBusqueda(null);
                setKeyboardIndex(-1);
                setMostrarDropdown(true);
            } catch (error) {
                // Fix bloqueante (review frontend, 2026-08-04): un error real (red caída,
                // 403 por permisos, servidor caído) NO es lo mismo que "no hay clientes
                // con ese nombre" — no bloqueamos al vendedor (puede seguir escribiendo o
                // ir a "crear cliente nuevo"), pero el mensaje tiene que decir la verdad,
                // sin detalle técnico (getApiErrorMessage ya lo traduce a texto de negocio).
                if (cancelado) return;
                setResultados([]);
                setErrorBusqueda(getApiErrorMessage(error, "No pudimos buscar clientes. Probá de nuevo."));
                setMostrarDropdown(true);
            } finally {
                if (!cancelado) setBuscando(false);
            }
        })();

        return () => { cancelado = true; };
    }, [debouncedBusqueda]);

    const handleSeleccionarCliente = (cliente) => {
        usuarioEscribiendo.current = false;
        setClienteSeleccionado(cliente);
        setBusqueda(cliente.fullName);
        setMostrarDropdown(false);
        setKeyboardIndex(-1);
    };

    const abrirAltaClienteNuevo = () => {
        setMostrarDropdown(false);
        setKeyboardIndex(-1);
        setModalClienteNuevoAbierto(true);
    };

    /**
     * Guarda el cliente nuevo con el MISMO endpoint que usa la pantalla de
     * Clientes (useCustomers.handleSaveCustomer) para el alta. Lo llamamos
     * directo acá (sin pasar por ese hook) porque esta fila no necesita
     * refrescar ningún listado de clientes — solo precisa el objeto recién
     * creado para dejarlo seleccionado. Los errores NO se atrapan acá: se
     * relanzan para que CustomerFormModal los muestre en su propio cartel
     * (mismo patrón P-6/P-7 que ya usa esa pantalla).
     */
    const handleClienteCreado = async (formData) => {
        const nuevoCliente = await api.post("/customers", formData);
        showSuccess("Cliente creado exitosamente");
        handleSeleccionarCliente(nuevoCliente);
        setModalClienteNuevoAbierto(false);
    };

    const handleCrear = async () => {
        if (!clienteSeleccionado) {
            showError("Elegí el cliente de la reserva.");
            return;
        }
        setCreando(true);
        try {
            const res = await api.post("/reservas", {
                name: "",
                payerId: getPublicId(clienteSeleccionado),
                startDate: startDate ? new Date(startDate).toISOString() : null,
            });
            showSuccess("Presupuesto creado");
            onCreada(getPublicId(res));
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudo crear la reserva."));
        } finally {
            setCreando(false);
        }
    };

    // Navegación por teclado del combobox: mismo patrón que ProductSearchField
    // (flechas mueven el resaltado, Enter elige, Escape cierra) para que el
    // buscador se pueda usar sin mouse.
    const handleKeyDown = (event) => {
        if (!mostrarDropdown) return;
        const totalOpciones = resultados.length + 1; // +1 = "crear cliente nuevo"

        if (event.key === "ArrowDown") {
            event.preventDefault();
            setKeyboardIndex((prev) => (prev < totalOpciones - 1 ? prev + 1 : 0));
        } else if (event.key === "ArrowUp") {
            event.preventDefault();
            setKeyboardIndex((prev) => (prev > 0 ? prev - 1 : totalOpciones - 1));
        } else if (event.key === "Enter" && keyboardIndex >= 0) {
            event.preventDefault();
            if (keyboardIndex < resultados.length) {
                handleSeleccionarCliente(resultados[keyboardIndex]);
            } else {
                abrirAltaClienteNuevo();
            }
        } else if (event.key === "Escape") {
            event.preventDefault();
            setMostrarDropdown(false);
            setKeyboardIndex(-1);
        }
    };

    const mostrarOpcionCrear = !buscando && busqueda.trim().length >= MIN_QUERY_LENGTH;

    return (
        <div
            // Fix review (2026-08-11, I5): el marco era índigo — pasa a la familia del
            // azul boleto (blue-*), el único color de acción del lavado de cara.
            className="rounded-2xl border-2 border-blue-300 bg-blue-50/40 p-4 dark:border-blue-700 dark:bg-blue-950/20"
            data-testid="fila-nueva-reserva"
        >
            <div className="mb-3 text-sm font-bold text-slate-800 dark:text-slate-100">Nuevo presupuesto</div>

            <div className="relative mb-4">
                <label htmlFor="nueva-reserva-cliente" className="mb-1 block text-xs font-bold text-slate-500 dark:text-slate-400">
                    Cliente
                </label>
                <div className="relative">
                    <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                    <input
                        id="nueva-reserva-cliente"
                        type="text"
                        role="combobox"
                        aria-expanded={mostrarDropdown}
                        aria-autocomplete="list"
                        aria-controls={listboxId.current}
                        autoComplete="off"
                        placeholder="Buscá por nombre o documento…"
                        value={busqueda}
                        onChange={(event) => {
                            usuarioEscribiendo.current = true;
                            // Si el vendedor edita el texto, la selección previa deja de valer
                            // hasta que elija de nuevo (evita crear la reserva con un cliente
                            // que ya no coincide con lo que está escrito en el casillero).
                            setClienteSeleccionado(null);
                            setBusqueda(event.target.value);
                        }}
                        onFocus={() => {
                            clearTimeout(blurTimer.current);
                            if (usuarioEscribiendo.current && busqueda.trim().length >= MIN_QUERY_LENGTH) {
                                setMostrarDropdown(true);
                            }
                        }}
                        onBlur={() => {
                            // Delay corto para que el click en un resultado no se cancele por el blur.
                            blurTimer.current = setTimeout(() => {
                                setMostrarDropdown(false);
                                setKeyboardIndex(-1);
                            }, 150);
                        }}
                        onKeyDown={handleKeyDown}
                        data-testid="nueva-reserva-cliente-input"
                        // Lavado de cara (2026-08-11, D residuos de la review T1): el aro de foco
                        // pasa de índigo suelto a los tokens del sistema (--primary/--ring, azul
                        // boleto) — mismo azul que ya usan los botones y el resto de la app.
                        className="w-full rounded-lg border border-slate-300 bg-white py-2 pl-9 pr-3 text-sm text-slate-800 outline-none focus:border-primary focus:ring-1 focus:ring-ring dark:border-slate-700 dark:bg-slate-900 dark:text-white"
                    />
                </div>

                {mostrarDropdown && (
                    <div
                        id={listboxId.current}
                        role="listbox"
                        aria-label="Resultados de búsqueda de clientes"
                        className="absolute left-0 right-0 top-full z-20 mt-1 max-h-72 overflow-y-auto rounded-lg border border-slate-200 bg-white shadow-xl dark:border-slate-700 dark:bg-slate-900"
                    >
                        {buscando && (
                            <div className="px-3 py-2 text-xs italic text-slate-400" role="status">
                                Buscando…
                            </div>
                        )}

                        {!buscando && resultados.map((cliente, index) => (
                            <button
                                key={getPublicId(cliente)}
                                type="button"
                                role="option"
                                aria-selected={keyboardIndex === index}
                                onMouseDown={(event) => event.preventDefault()}
                                onClick={() => handleSeleccionarCliente(cliente)}
                                data-testid="nueva-reserva-cliente-resultado"
                                // Resaltado del resultado seleccionado con teclado: tinte del azul
                                // boleto del sistema (bg-primary/…) en vez de índigo suelto.
                                className={`block w-full border-b border-slate-100 px-3 py-2 text-left text-sm dark:border-slate-800 ${
                                    keyboardIndex === index ? "bg-primary/10 dark:bg-primary/20" : "hover:bg-slate-50 dark:hover:bg-slate-800"
                                }`}
                            >
                                <span className="font-semibold text-slate-800 dark:text-slate-100">{cliente.fullName}</span>
                                {cliente.documentNumber ? (
                                    <span className="ml-2 text-xs text-slate-400">
                                        · {cliente.documentType || "DNI"} {cliente.documentNumber}
                                    </span>
                                ) : null}
                            </button>
                        ))}

                        {/* Fix bloqueante (review frontend, 2026-08-04): error de red/permiso ≠
                            "no hay resultados" — dos mensajes distintos, nunca el mismo texto. */}
                        {!buscando && errorBusqueda && (
                            <div
                                className="px-3 py-2 text-xs font-semibold text-rose-600 dark:text-rose-400"
                                role="alert"
                                data-testid="nueva-reserva-cliente-error"
                            >
                                {errorBusqueda}
                            </div>
                        )}

                        {!buscando && !errorBusqueda && resultados.length === 0 && (
                            <div className="px-3 py-2 text-xs text-slate-400" role="status">
                                No encontramos a nadie con "{busqueda.trim()}"
                            </div>
                        )}

                        {mostrarOpcionCrear && (
                            <button
                                type="button"
                                role="option"
                                aria-selected={keyboardIndex === resultados.length}
                                onMouseDown={(event) => event.preventDefault()}
                                onClick={abrirAltaClienteNuevo}
                                data-testid="nueva-reserva-cliente-nuevo"
                                // Fix bloqueante de review (2026-08-11, B3): faltaba el dark: explícito
                                // — sobre el fondo casi negro del dropdown en oscuro, un 10%/5% de tinte
                                // azul se notaba muy poco. Mismo par de valores (20%/10%) que ya usa el
                                // resaltado por teclado de los resultados, unas líneas más arriba.
                                className={`block w-full px-3 py-2 text-left text-sm font-semibold text-primary ${
                                    keyboardIndex === resultados.length
                                        ? "bg-primary/10 dark:bg-primary/20"
                                        : "hover:bg-primary/5 dark:hover:bg-primary/10"
                                }`}
                            >
                                + Es un cliente nuevo: crearlo acá
                            </button>
                        )}
                    </div>
                )}
            </div>

            <div className="flex flex-wrap items-end gap-3">
                <div>
                    <label htmlFor="nueva-reserva-salida" className="mb-1 block text-xs font-bold text-slate-500 dark:text-slate-400">
                        Salida
                    </label>
                    <input
                        id="nueva-reserva-salida"
                        type="date"
                        value={startDate}
                        onChange={(event) => setStartDate(event.target.value)}
                        className="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-800 outline-none focus:border-primary focus:ring-1 focus:ring-ring dark:border-slate-700 dark:bg-slate-900 dark:text-white"
                    />
                </div>
                <div className="flex-1" />
                <button
                    type="button"
                    onClick={onCancelar}
                    className="rounded-lg px-4 py-2 text-sm font-semibold text-slate-600 transition-colors hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800"
                >
                    Cancelar
                </button>
                <button
                    type="button"
                    onClick={handleCrear}
                    disabled={creando}
                    data-testid="nueva-reserva-crear-boton"
                    // Fix review (2026-08-11, I5): el relleno era índigo — pasa al token
                    // `primary` (azul boleto), el único color de acción de la app.
                    className="rounded-lg bg-primary px-5 py-2 text-sm font-bold text-primary-foreground shadow-sm transition-colors hover:bg-primary/90 disabled:cursor-not-allowed disabled:opacity-60"
                >
                    {creando ? "Creando…" : "Crear presupuesto"}
                </button>
            </div>

            {/* Fix bloqueante (review frontend, 2026-08-04): CustomerFormModal se monta
                SOLO mientras está abierto (igual que CustomersPage.jsx). Ese modal
                inicializa su formulario con useState UNA vez, al montarse — si lo
                dejábamos siempre montado, cerrar y reabrir mostraba los datos de la
                ÚLTIMA vez (riesgo de crear un cliente duplicado o con datos de otro
                cliente sin querer). Remontarlo de cero en cada apertura le da un
                formulario limpio siempre. */}
            {modalClienteNuevoAbierto && (
                <CustomerFormModal
                    isOpen={modalClienteNuevoAbierto}
                    onClose={() => setModalClienteNuevoAbierto(false)}
                    customer={null}
                    onSave={handleClienteCreado}
                />
            )}
        </div>
    );
}
