/**
 * Tarifario — "la memoria de lo que ya vendiste" (spec firmada 2026-08-06, ampliada
 * 2026-08-07 con variantes y la bandeja Repetidos, y 2026-08-08 con la solapa Excursiones).
 *
 * Solapas SIEMPRE las que mande el motor (Hoteles/Aéreos/Paquetes/Traslados/Asistencias/
 * Excursiones — V17=C) con su conteo, más una solapa aparte "Repetidos" para la bandeja
 * de revisión (V8=A / V11=B). Murió el desplegable "Tipo": la solapa ya dice qué se está
 * mirando (P-16). Nunca se hardcodea la cantidad acá — ver `resolveTabsForRender`.
 *
 * Dentro de cada solapa, los productos vienen agrupados por VARIANTE (habitación,
 * cabina o vehículo según el tipo) y, adentro, por operador — ver LearnedProductRow.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { DollarSign, Plus, Search } from "lucide-react";
import { api } from "../api";
import { useDebounce } from "../hooks/useDebounce";
import { PaginationFooter } from "../components/ui/PaginationFooter";
import { SkeletonTableRow } from "../components/ui/skeleton";
import { ListEmptyState } from "../components/ui/ListEmptyState";
import { ListLoadErrorState } from "../components/ui/ListLoadErrorState";
import { hasPermission } from "../auth";
import { AddProductInlineForm } from "../features/rates/components/AddProductInlineForm";
import { ProductInlineEditForm } from "../features/rates/components/ProductInlineEditForm";
import { LearnedProductRow } from "../features/rates/components/LearnedProductRow";
import { DuplicatesTray } from "../features/rates/components/DuplicatesTray";
import { pickDefaultServiceTypeTab, columnLabelsForServiceType, emptyTabMessage, resolveTabsForRender } from "../features/rates/lib/learnedProductVariantsLogic";

const SELECT_CLASS = "rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200";

// Solapa especial, aparte de las cinco de tipo de servicio (no es un ServiceType real).
const REPEATED_TAB_KEY = "Repetidos";

export default function RatesPage() {
    const navigate = useNavigate();
    // Fix 2026-08-07 (review P-9): crear un producto (POST /rates/simple) pide
    // tarifario.edit en el servidor — igual que renombrar. Sin este permiso, cualquier
    // disparador del alta (botón de cabecera o el del estado vacío) queda escondido,
    // en vez de dejar que el usuario llene la fichita y coma un 403.
    const puedeAgregarProducto = hasPermission("tarifario.edit");

    const [search, setSearch] = useState("");
    const debouncedSearch = useDebounce(search, 300);
    const [filterSupplierId, setFilterSupplierId] = useState("");
    const [page, setPage] = useState(1);
    const [pageSize, setPageSize] = useState(25);

    // Solapa activa: arranca en "Hotel" (primer tipo del mockup) y, apenas llega la
    // primera respuesta con los conteos reales, se corrige sola a la primera solapa CON
    // productos — pero SOLO si el usuario todavía no tocó ninguna solapa a mano.
    const [activeTab, setActiveTab] = useState("Hotel");
    const usuarioEligioSolapaRef = useRef(false);

    const [items, setItems] = useState([]);
    const [tabs, setTabs] = useState([]);
    const [repetidosCount, setRepetidosCount] = useState(0);
    const [pageState, setPageState] = useState({ totalCount: 0, totalPages: 0, hasPreviousPage: false, hasNextPage: false });
    const [loading, setLoading] = useState(true);
    const [loadError, setLoadError] = useState(false);

    const [suppliers, setSuppliers] = useState([]);
    const [showAddForm, setShowAddForm] = useState(false);
    const [expandedProductId, setExpandedProductId] = useState(null);

    const esBandejaRepetidos = activeTab === REPEATED_TAB_KEY;

    const loadProducts = useCallback(async () => {
        if (esBandejaRepetidos) return; // la bandeja se autoabastece (DuplicatesTray)
        setLoading(true);
        setLoadError(false);
        try {
            const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize), serviceType: activeTab });
            if (debouncedSearch.trim()) params.set("search", debouncedSearch.trim());
            if (filterSupplierId) params.set("supplierId", filterSupplierId);
            const response = await api.get(`/rates/learned-products?${params.toString()}`);
            setItems(Array.isArray(response?.items) ? response.items : []);
            setTabs(Array.isArray(response?.tabs) ? response.tabs : []);
            setPageState({
                totalCount: response?.totalCount ?? 0,
                totalPages: response?.totalPages ?? 0,
                hasPreviousPage: Boolean(response?.hasPreviousPage),
                hasNextPage: Boolean(response?.hasNextPage),
            });
            // Primera carga (usuario todavía no eligió solapa): si "Hotel" está en cero y
            // hay otro tipo con productos, nos paramos ahí (V8=A: nunca aterrizar vacío).
            // Fix ronda 2 de review: los conteos de `tabs` viajan FILTRADOS por lo que hay
            // tipeado en el buscador (el motor cuenta "todo lo que matchea la búsqueda", no
            // todo el tarifario) — auto-saltar mientras hay texto tiraba a una solapa que
            // solo tenía resultados por casualidad de esa búsqueda puntual (ej: escribir
            // "van" en Hoteles saltaba a Traslados). Con texto de búsqueda, el usuario se
            // queda donde está.
            if (!usuarioEligioSolapaRef.current && !debouncedSearch.trim() && Array.isArray(response?.tabs)) {
                const sugerida = pickDefaultServiceTypeTab(response.tabs);
                if (sugerida && sugerida !== activeTab) {
                    setActiveTab(sugerida);
                }
            }
        } catch {
            setItems([]);
            setLoadError(true);
        } finally {
            setLoading(false);
        }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [debouncedSearch, filterSupplierId, page, pageSize, activeTab, esBandejaRepetidos]);

    useEffect(() => {
        loadProducts();
    }, [loadProducts]);

    // Deps []: la lista de operadores es de catálogo (para el filtro y la fichita), no
    // depende de la búsqueda/paginado — se carga una sola vez al montar la pantalla.
    useEffect(() => {
        api.get("/suppliers?page=1&pageSize=100&includeInactive=true")
            .then((data) => setSuppliers(data?.items || []))
            .catch(() => setSuppliers([]));
        // El conteo de la solapa "Repetidos" sale de su propio endpoint (no viaja en
        // `tabs`, que son solo los cinco tipos de servicio). Se pide una vez al entrar;
        // la bandeja en sí vuelve a pedir sus datos completos cuando el usuario la abre.
        api.get("/rates/duplicates")
            .then((data) => setRepetidosCount(data?.groups?.length || 0))
            .catch(() => setRepetidosCount(0));
    }, []);

    useEffect(() => {
        setPage(1);
    }, [debouncedSearch, filterSupplierId, activeTab]);

    const seleccionarSolapa = (tabKey) => {
        usuarioEligioSolapaRef.current = true;
        setActiveTab(tabKey);
        setExpandedProductId(null);
    };

    const abrirCargaCompleta = (prefill) => {
        navigate("/rates/full", { state: { prefillFromRates: prefill } });
    };

    const handleProductoCreado = (creado) => {
        setShowAddForm(false);
        // Fix ronda 2 de review (P-11): "Producto guardado." no se veía si el vendedor
        // estaba parado en otra solapa cuando lo creó (ej: agregó un traslado estando en
        // Hoteles). Saltamos a la solapa del tipo recién creado para que el renglón nuevo
        // quede a la vista — mismo mecanismo que ya usa elegir una solapa a mano.
        if (creado?.serviceType && creado.serviceType !== activeTab) {
            seleccionarSolapa(creado.serviceType);
        } else {
            loadProducts();
        }
    };

    const handleExistenteElegido = (nombre) => {
        setShowAddForm(false);
        setSearch(nombre);
    };

    const { productColumnLabel, variantColumnLabel } = columnLabelsForServiceType(activeTab);
    const gridColumns = variantColumnLabel
        ? "grid grid-cols-[minmax(0,2fr)_minmax(0,1.4fr)_minmax(0,1.2fr)_minmax(0,1fr)_104px]"
        : "grid grid-cols-[minmax(0,2fr)_minmax(0,1.2fr)_minmax(0,1fr)_104px]";
    // Solo es true mientras el servidor NUNCA contestó (tabs sigue en su [] inicial): ahí
    // se pintan las solapas fijas de respaldo (resolveTabsForRender) y tienen que quedar
    // clickeables, no apagadas — apagar por count:0 solo vale para un conteo CONFIRMADO.
    const usandoSolapasFijasDeRespaldo = tabs.length === 0;

    return (
        <div className="space-y-6">
            <header className="flex flex-wrap items-center justify-between gap-3">
                <div>
                    <h1 className="flex items-center gap-3 text-2xl font-bold tracking-tight text-slate-900 dark:text-white">
                        <div className="rounded-xl bg-gradient-to-br from-emerald-500 to-teal-600 p-2.5 text-white shadow-lg shadow-emerald-500/20">
                            <DollarSign className="h-6 w-6" />
                        </div>
                        Tarifario
                    </h1>
                    <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                        Los productos que ya vendiste, con el último precio de cada operador.
                    </p>
                </div>
                {puedeAgregarProducto && !esBandejaRepetidos && (
                    <button
                        type="button"
                        onClick={() => setShowAddForm((prev) => !prev)}
                        className="flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-medium text-white shadow-sm shadow-indigo-500/20 hover:bg-indigo-500"
                        data-testid="add-product-button"
                    >
                        <Plus className="h-4 w-4" />
                        Agregar producto
                    </button>
                )}
            </header>

            {/* Solapas: las cinco de tipo de servicio + "Repetidos" aparte (V8=A / V11=B).
                Una solapa en cero se ve apagada pero VISIBLE — "cero" también es información
                (mismo criterio 2026-08-03 P3=B que ya usa el listado de Reservas). */}
            <div role="tablist" aria-label="Filtrar el tarifario" className="flex flex-wrap gap-x-1 overflow-x-auto border-b border-slate-200 scrollbar-hide dark:border-slate-800">
                {/* `tabs` (el state, no el resultado de resolveTabsForRender) solo queda
                    vacío cuando TODAVÍA no llegó ninguna respuesta real del servidor — ahí
                    se pintan las solapas fijas de respaldo con conteo 0 (`usandoSolapasFijasDeRespaldo`,
                    calculado más arriba). Retoque ronda 3: esas solapas de respaldo quedan
                    CLICKEABLES (el usuario puede querer mirar otro tipo mientras reintenta),
                    a diferencia de una solapa real en cero (esa sí se apaga: "cero" es
                    información confirmada por el servidor). */}
                {resolveTabsForRender(tabs).map((tab) => {
                    const apagada = !usandoSolapasFijasDeRespaldo && tab.count === 0;
                    const activa = activeTab === tab.serviceType;
                    return (
                        <button
                            key={tab.serviceType}
                            type="button"
                            role="tab"
                            aria-selected={activa}
                            disabled={apagada}
                            onClick={() => seleccionarSolapa(tab.serviceType)}
                            data-testid={`tab-tarifario-${tab.serviceType}`}
                            className={`flex items-center gap-1.5 whitespace-nowrap border-b-2 px-3 py-2 text-[13px] font-medium transition-colors disabled:cursor-not-allowed ${
                                activa
                                    ? "border-indigo-600 font-bold text-indigo-600 dark:border-indigo-400 dark:text-indigo-400"
                                    : apagada
                                        ? "border-transparent text-slate-400 opacity-55 dark:text-slate-600"
                                        : "border-transparent text-slate-600 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200"
                            }`}
                        >
                            {tab.label}
                            <span className={`rounded-full px-1.5 py-0.5 text-[11px] font-semibold ${activa ? "bg-indigo-50 text-indigo-600 dark:bg-indigo-900/40 dark:text-indigo-300" : "bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-400"}`}>
                                {tab.count}
                            </span>
                        </button>
                    );
                })}
                <button
                    type="button"
                    role="tab"
                    aria-selected={esBandejaRepetidos}
                    onClick={() => seleccionarSolapa(REPEATED_TAB_KEY)}
                    data-testid="tab-tarifario-Repetidos"
                    className={`ml-auto flex items-center gap-1.5 whitespace-nowrap border-b-2 px-3 py-2 text-[13px] font-medium transition-colors ${
                        esBandejaRepetidos
                            ? "border-indigo-600 font-bold text-indigo-600 dark:border-indigo-400 dark:text-indigo-400"
                            : "border-transparent text-slate-600 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200"
                    }`}
                >
                    Repetidos
                    <span className={`rounded-full px-1.5 py-0.5 text-[11px] font-semibold ${esBandejaRepetidos ? "bg-indigo-50 text-indigo-600 dark:bg-indigo-900/40 dark:text-indigo-300" : "bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-400"}`}>
                        {repetidosCount}
                    </span>
                </button>
            </div>

            {esBandejaRepetidos ? (
                <div className="rounded-2xl border border-slate-200 bg-white dark:border-slate-700 dark:bg-slate-900">
                    {/* El badge "Repetidos (N)" se pidió UNA vez al entrar (más abajo, useEffect
                        deps []); sin este aviso quedaba desactualizado toda la visita apenas se
                        resolvía un grupo acá adentro (fix ronda 2 de review). */}
                    <DuplicatesTray onRepetidosCambiaron={setRepetidosCount} />
                </div>
            ) : (
                <>
                    {puedeAgregarProducto && showAddForm && (
                        <AddProductInlineForm
                            suppliers={suppliers}
                            defaultServiceType={activeTab}
                            onCancel={() => setShowAddForm(false)}
                            onCreated={handleProductoCreado}
                            onExistingChosen={handleExistenteElegido}
                            onOpenCargaCompleta={abrirCargaCompleta}
                        />
                    )}

                    <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
                        <div className="relative flex-1 sm:max-w-md">
                            <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                            <input
                                type="text"
                                value={search}
                                onChange={(event) => setSearch(event.target.value)}
                                placeholder="Buscar hotel, vuelo, paquete…"
                                className="w-full rounded-lg border border-slate-200 bg-white py-2 pl-10 pr-3 text-sm dark:border-slate-700 dark:bg-slate-900 dark:text-white"
                            />
                        </div>
                        <select value={filterSupplierId} onChange={(event) => setFilterSupplierId(event.target.value)} className={SELECT_CLASS}>
                            <option value="">Todos los operadores</option>
                            {suppliers.map((supplier) => (
                                <option key={supplier.publicId || supplier.PublicId} value={supplier.publicId || supplier.PublicId}>{supplier.name}</option>
                            ))}
                        </select>
                    </div>

                    <div className="rounded-2xl border border-slate-200 bg-white dark:border-slate-700 dark:bg-slate-900">
                        <div className={`${gridColumns} gap-3 border-b border-slate-100 px-6 py-3 text-xs font-bold uppercase tracking-wider text-slate-400 dark:border-slate-800`}>
                            <div>{productColumnLabel}</div>
                            {variantColumnLabel && <div>{variantColumnLabel}</div>}
                            <div>Operador</div>
                            <div>Precio</div>
                            <div>Cuándo</div>
                        </div>

                        {loading ? (
                            // Renglones sueltos, SIN el wrapper con borde propio de <SkeletonTable>:
                            // este contenedor ya tiene su propia tarjeta (rounded-2xl border más
                            // arriba) — usar el componente completo dibujaba una tarjeta adentro de
                            // otra tarjeta (hallazgo de review 2026-08-07).
                            Array.from({ length: 5 }).map((_, index) => <SkeletonTableRow key={index} cols={variantColumnLabel ? 5 : 4} />)
                        ) : loadError ? (
                            <div className="p-6">
                                <ListLoadErrorState message="No se pudo cargar el tarifario." onRetry={loadProducts} />
                            </div>
                        ) : items.length === 0 ? (
                            <ListEmptyState
                                title={debouncedSearch.trim() ? `No encontramos "${debouncedSearch.trim()}" en tu tarifario.` : emptyTabMessage(activeTab)}
                                action={
                                    puedeAgregarProducto ? (
                                        <button
                                            type="button"
                                            onClick={() => setShowAddForm(true)}
                                            className="flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-500"
                                        >
                                            <Plus className="h-4 w-4" />
                                            Agregar producto
                                        </button>
                                    ) : null
                                }
                            />
                        ) : (
                            <div className="divide-y divide-slate-100 dark:divide-slate-800">
                                {items.map((product) => {
                                    // Id estable para el link ARIA botón↔panel (disclosure pattern):
                                    // el botón dice QUÉ región controla aunque esa región todavía no
                                    // esté montada (colapsada).
                                    const panelId = `product-edit-panel-${product.productPublicId}`;
                                    const isExpanded = expandedProductId === product.productPublicId;
                                    return (
                                        <div key={product.productPublicId}>
                                            <LearnedProductRow
                                                product={product}
                                                isExpanded={isExpanded}
                                                panelId={panelId}
                                                onToggle={() => setExpandedProductId((current) => (current === product.productPublicId ? null : product.productPublicId))}
                                            />
                                            {isExpanded && (
                                                <ProductInlineEditForm
                                                    product={product}
                                                    panelId={panelId}
                                                    onCancel={() => setExpandedProductId(null)}
                                                    onSaved={() => {
                                                        setExpandedProductId(null);
                                                        loadProducts();
                                                    }}
                                                />
                                            )}
                                        </div>
                                    );
                                })}
                            </div>
                        )}
                    </div>

                    {!loading && !loadError && items.length > 0 && (
                        <PaginationFooter
                            page={page}
                            pageSize={pageSize}
                            totalCount={pageState.totalCount}
                            totalPages={pageState.totalPages}
                            hasPreviousPage={pageState.hasPreviousPage}
                            hasNextPage={pageState.hasNextPage}
                            onPageChange={setPage}
                            onPageSizeChange={setPageSize}
                        />
                    )}
                </>
            )}
        </div>
    );
}
