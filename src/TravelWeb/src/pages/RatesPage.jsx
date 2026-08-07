/**
 * Tarifario — "la memoria de lo que ya vendiste" (spec firmada 2026-08-06).
 *
 * Lista de productos aprendidos de las ventas, con un renglón por operador (último
 * precio, moneda, fecha). Reemplaza al viejo Tarifario de 20 campos: el alta a mano
 * pasa a ser una fichita de pocos campos ("+ Agregar producto"), y el formulario largo
 * de siempre queda vivo solo detrás de "Carga completa" (vigencias, variaciones de
 * habitación) — ver RatesFullFormPage.jsx.
 */
import { useCallback, useEffect, useState } from "react";
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

const SERVICE_TYPE_FILTER_OPTIONS = [
    { value: "", label: "Todos" },
    { value: "Hotel", label: "Hotel" },
    { value: "Aereo", label: "Aéreo" },
    { value: "Traslado", label: "Traslado" },
    { value: "Paquete", label: "Paquete" },
    { value: "Asistencia", label: "Asistencia" },
    { value: "Excursion", label: "Excursión" },
    { value: "Otro", label: "Otro" },
];

const SELECT_CLASS = "rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200";

export default function RatesPage() {
    const navigate = useNavigate();
    // Fix 2026-08-07 (review P-9): crear un producto (POST /rates/simple) pide
    // tarifario.edit en el servidor — igual que renombrar. Sin este permiso, cualquier
    // disparador del alta (botón de cabecera o el del estado vacío) queda escondido,
    // en vez de dejar que el usuario llene la fichita y coma un 403.
    const puedeAgregarProducto = hasPermission("tarifario.edit");

    const [search, setSearch] = useState("");
    const debouncedSearch = useDebounce(search, 300);
    const [filterType, setFilterType] = useState("");
    const [filterSupplierId, setFilterSupplierId] = useState("");
    const [page, setPage] = useState(1);
    const [pageSize, setPageSize] = useState(25);

    const [items, setItems] = useState([]);
    const [pageState, setPageState] = useState({ totalCount: 0, totalPages: 0, hasPreviousPage: false, hasNextPage: false });
    const [loading, setLoading] = useState(true);
    const [loadError, setLoadError] = useState(false);

    const [suppliers, setSuppliers] = useState([]);
    const [showAddForm, setShowAddForm] = useState(false);
    const [expandedProductId, setExpandedProductId] = useState(null);

    const loadProducts = useCallback(async () => {
        setLoading(true);
        setLoadError(false);
        try {
            const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
            if (debouncedSearch.trim()) params.set("search", debouncedSearch.trim());
            if (filterType) params.set("serviceType", filterType);
            if (filterSupplierId) params.set("supplierId", filterSupplierId);
            const response = await api.get(`/rates/learned-products?${params.toString()}`);
            setItems(Array.isArray(response?.items) ? response.items : []);
            setPageState({
                totalCount: response?.totalCount ?? 0,
                totalPages: response?.totalPages ?? 0,
                hasPreviousPage: Boolean(response?.hasPreviousPage),
                hasNextPage: Boolean(response?.hasNextPage),
            });
        } catch {
            setItems([]);
            setLoadError(true);
        } finally {
            setLoading(false);
        }
    }, [debouncedSearch, filterType, filterSupplierId, page, pageSize]);

    useEffect(() => {
        loadProducts();
    }, [loadProducts]);

    // Deps []: la lista de operadores es de catálogo (para el filtro y la fichita), no
    // depende de la búsqueda/paginado — se carga una sola vez al montar la pantalla.
    useEffect(() => {
        api.get("/suppliers?page=1&pageSize=100&includeInactive=true")
            .then((data) => setSuppliers(data?.items || []))
            .catch(() => setSuppliers([]));
    }, []);

    useEffect(() => {
        setPage(1);
    }, [debouncedSearch, filterType, filterSupplierId]);

    const abrirCargaCompleta = (prefill) => {
        navigate("/rates/full", { state: { prefillFromRates: prefill } });
    };

    const handleProductoCreado = () => {
        setShowAddForm(false);
        loadProducts();
    };

    const handleExistenteElegido = (nombre) => {
        setShowAddForm(false);
        setSearch(nombre);
    };

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
                {puedeAgregarProducto && (
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

            {puedeAgregarProducto && showAddForm && (
                <AddProductInlineForm
                    suppliers={suppliers}
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
                <select value={filterType} onChange={(event) => setFilterType(event.target.value)} className={SELECT_CLASS}>
                    {SERVICE_TYPE_FILTER_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                </select>
                <select value={filterSupplierId} onChange={(event) => setFilterSupplierId(event.target.value)} className={SELECT_CLASS}>
                    <option value="">Todos los operadores</option>
                    {suppliers.map((supplier) => (
                        <option key={supplier.publicId || supplier.PublicId} value={supplier.publicId || supplier.PublicId}>{supplier.name}</option>
                    ))}
                </select>
            </div>

            <div className="rounded-2xl border border-slate-200 bg-white dark:border-slate-700 dark:bg-slate-900">
                <div className="grid grid-cols-[minmax(0,2fr)_88px_minmax(0,1.2fr)_minmax(0,1fr)_104px] gap-3 border-b border-slate-100 px-6 py-3 text-xs font-bold uppercase tracking-wider text-slate-400 dark:border-slate-800">
                    <div>Producto</div>
                    <div>Tipo</div>
                    <div>Operador</div>
                    <div>Último precio</div>
                    <div>Cuándo</div>
                </div>

                {loading ? (
                    // Renglones sueltos, SIN el wrapper con borde propio de <SkeletonTable>:
                    // este contenedor ya tiene su propia tarjeta (rounded-2xl border más
                    // arriba) — usar el componente completo dibujaba una tarjeta adentro de
                    // otra tarjeta (hallazgo de review 2026-08-07).
                    Array.from({ length: 5 }).map((_, index) => <SkeletonTableRow key={index} cols={5} />)
                ) : loadError ? (
                    <div className="p-6">
                        <ListLoadErrorState message="No se pudo cargar el tarifario." onRetry={loadProducts} />
                    </div>
                ) : items.length === 0 ? (
                    <ListEmptyState
                        title={debouncedSearch.trim() ? `No encontramos "${debouncedSearch.trim()}" en tu tarifario.` : "Todavía no hay productos."}
                        description={debouncedSearch.trim() ? null : "El tarifario se arma solo: la primera vez que cargues un servicio, el producto queda guardado acá."}
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
        </div>
    );
}
