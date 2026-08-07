/**
 * Ficha en línea de un producto del Tarifario (spec firmada 2026-08-06, §2.2): se abre
 * debajo del renglón al tocarlo. Permite corregir nombre y ciudad, muestra la memoria de
 * precios que aprendió de las ventas, y ofrece "Carga completa" para el formulario largo.
 *
 * Los precios NO se editan acá (son la memoria de lo que pasó, se cambian vendiendo).
 *
 * Permiso (fix 2026-08-07, decisión firmada del dueño): renombrar un producto pide
 * `tarifario.edit` — el mismo mecanismo que gatea el resto de las pantallas (no
 * Admin-only; el backend dejó de exigir rol Admin acá). Sin ese permiso, nombre/ciudad
 * se muestran de solo lectura y no aparece el botón Guardar.
 */
import { useEffect, useRef, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { api } from "../../../api";
import { showSuccess } from "../../../alerts";
import { hasPermission } from "../../../auth";
import {
    buildSupplierPriceLineText,
    buildRenameLearnedProductPayload,
    validateProductNameAndCity,
} from "../lib/ratesLearnedProductsLogic";

const INPUT_CLASS = "w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-900 dark:text-white";
const LABEL_CLASS = "block text-xs font-semibold text-slate-600 dark:text-slate-400 mb-1";

export function ProductInlineEditForm({ product, panelId, onCancel, onSaved }) {
    const navigate = useNavigate();
    const esHotel = product.serviceType === "Hotel";
    const puedeEditar = hasPermission("tarifario.edit");
    const primerCampoRef = useRef(null);

    const [nombre, setNombre] = useState(product.name || "");
    const [ciudad, setCiudad] = useState(esHotel ? (product.subtitle || "") : "");
    const [errors, setErrors] = useState({});
    const [guardando, setGuardando] = useState(false);
    const [errorGuardar, setErrorGuardar] = useState(null);

    // Foco al primer campo apenas se abre la ficha (a11y: el usuario no tiene que
    // buscarlo con el mouse/tab). Deps []: solo nos interesa el momento del montaje,
    // no cada vez que puedeEditar/nombre cambian.
    useEffect(() => {
        primerCampoRef.current?.focus();
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    const handleGuardar = async () => {
        const validacion = validateProductNameAndCity({ serviceType: product.serviceType, name: nombre, city: ciudad });
        setErrors(validacion);
        if (Object.keys(validacion).length > 0) return;

        setGuardando(true);
        setErrorGuardar(null);
        try {
            const payload = buildRenameLearnedProductPayload({
                serviceType: product.serviceType,
                currentName: product.name,
                currentCity: esHotel ? product.subtitle : null,
                newName: nombre,
                newCity: ciudad,
            });
            const resultado = await api.post("/rates/learned-products/rename", payload);
            showSuccess("Producto guardado.");
            onSaved(resultado);
        } catch (err) {
            // 409 "NombreYaUsado"/400/404: el backend ya arma un mensaje pensado para el
            // usuario — lo mostramos tal cual, sin fusionar nada (no es el freno de
            // repetidos del alta, es un rechazo directo). Sin mensaje del servidor (error
            // de red, 500), cae al texto genérico de la Ronda 2 (2026-06-06).
            setErrorGuardar(err.payload?.message || "No se pudo guardar. Revisá la conexión y probá de nuevo.");
        } finally {
            setGuardando(false);
        }
    };

    const irACargaCompleta = () => {
        // El precio más nuevo (primer elemento, ya vienen ordenados) sirve de punto de
        // partida para el formulario largo — "sin perder nada de lo tipeado" (§2.3).
        const referencia = product.suppliers?.[0];
        navigate("/rates/full", {
            state: {
                prefillFromRates: {
                    serviceType: product.serviceType,
                    name: nombre,
                    city: esHotel ? ciudad : "",
                    supplierId: referencia?.supplierPublicId ? String(referencia.supplierPublicId) : "",
                    price: referencia?.price,
                    currency: referencia?.currency,
                },
            },
        });
    };

    return (
        <div
            id={panelId}
            className="border-t border-slate-100 bg-slate-50/60 px-6 py-4 dark:border-slate-800 dark:bg-slate-900/40"
            data-testid="product-inline-edit-form"
        >
            <div className={`grid gap-3 ${esHotel ? "sm:grid-cols-2" : "sm:grid-cols-1 sm:max-w-sm"}`}>
                <div>
                    <label className={LABEL_CLASS} htmlFor="edit-product-name">Nombre *</label>
                    {puedeEditar ? (
                        <input
                            id="edit-product-name"
                            ref={primerCampoRef}
                            className={INPUT_CLASS}
                            value={nombre}
                            onChange={(event) => setNombre(event.target.value)}
                            aria-invalid={Boolean(errors.name)}
                        />
                    ) : (
                        <p className="text-sm text-slate-700 dark:text-slate-200">{nombre}</p>
                    )}
                    {errors.name && <p className="mt-1 text-xs text-rose-600">{errors.name}</p>}
                </div>
                {esHotel && (
                    <div>
                        <label className={LABEL_CLASS} htmlFor="edit-product-city">Ciudad *</label>
                        {puedeEditar ? (
                            <input
                                id="edit-product-city"
                                className={INPUT_CLASS}
                                value={ciudad}
                                onChange={(event) => setCiudad(event.target.value)}
                                aria-invalid={Boolean(errors.city)}
                            />
                        ) : (
                            <p className="text-sm text-slate-700 dark:text-slate-200">{ciudad}</p>
                        )}
                        {errors.city && <p className="mt-1 text-xs text-rose-600">{errors.city}</p>}
                    </div>
                )}
            </div>

            <div className="mt-4">
                <p className="mb-2 text-xs font-semibold uppercase tracking-wider text-slate-400">
                    Precios que aprendió de tus ventas
                </p>
                <div className="space-y-1.5">
                    {(product.suppliers || []).map((supplierPrice, index) => (
                        <div
                            key={`${supplierPrice.supplierPublicId ?? "sin-operador"}-${index}`}
                            className="flex items-center justify-between gap-3 text-sm text-slate-600 dark:text-slate-300"
                        >
                            <span>{buildSupplierPriceLineText(supplierPrice)}</span>
                            {supplierPrice.numeroReserva && supplierPrice.reservaPublicId && (
                                <Link
                                    to={`/reservas/${supplierPrice.reservaPublicId}`}
                                    className="shrink-0 text-xs font-semibold text-indigo-600 hover:underline dark:text-indigo-400"
                                >
                                    {supplierPrice.numeroReserva}
                                </Link>
                            )}
                        </div>
                    ))}
                </div>
            </div>

            {errorGuardar && (
                <p className="mt-3 rounded-lg bg-rose-50 px-3 py-2 text-xs font-semibold text-rose-700 dark:bg-rose-900/20 dark:text-rose-300">
                    {errorGuardar}
                </p>
            )}

            <div className="mt-4 flex items-center justify-between">
                <button
                    type="button"
                    onClick={irACargaCompleta}
                    className="text-xs font-semibold text-slate-500 hover:text-indigo-600 dark:text-slate-400"
                >
                    Carga completa
                </button>
                <div className="flex gap-2">
                    <button
                        type="button"
                        onClick={onCancel}
                        className="rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
                    >
                        Cancelar
                    </button>
                    {puedeEditar && (
                        <button
                            type="button"
                            onClick={handleGuardar}
                            disabled={guardando}
                            className="rounded-lg bg-indigo-600 px-4 py-1.5 text-xs font-semibold text-white hover:bg-indigo-700 disabled:opacity-60"
                        >
                            {guardando ? "Guardando..." : "Guardar"}
                        </button>
                    )}
                </div>
            </div>
        </div>
    );
}
