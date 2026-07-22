import React, { useState, useEffect } from 'react';
import { Wallet } from "lucide-react";
import { Badge } from "../../../components/ui/badge";
import { formatCurrency } from "../../../lib/utils";
import { getPublicId } from "../../../lib/publicIds";
// Reutilizamos CURRENCY_OPTIONS del alta para que las etiquetas sean idénticas en ambas superficies.
import { CURRENCY_OPTIONS } from "../lib/nuevoOperadorLogic.js";
// Mismo helper que usa SupplierTable: el escalar currentBalance no tiene moneda propia
// (puede mezclar ARS con USD), por eso el saldo real se arma por moneda desde balancesByCurrency.
import { supplierBalanceLines } from "../lib/supplierBalanceView";
// Este modal no muestra el desplegable de "¿Suele cobrar multa?" — solo necesita
// normalizar el valor para el round-trip (ver comentario del estado inicial).
import { SUPPLIER_PENALTY_BEHAVIOR, valorSelectDesdePenaltyBehavior } from "../../../lib/supplierPenaltyBehavior.js";

export function SupplierFormModal({ isOpen, onClose, supplier, onSave }) {
    const [formData, setFormData] = useState({
        name: "",
        contactName: "",
        taxId: "",
        taxCondition: "",
        address: "",
        email: "",
        phone: "",
        isActive: true,
        currentBalance: 0,
        // Moneda por defecto del carril de cuenta corriente del operador.
        defaultCurrency: "ARS",
        // Round-trip: se preserva en el PUT para no perder el plazo acordado (ADR-041).
        defaultPaymentTermDays: null,
        // ADR-044 T4 (2026-07-10, gate de frontend — round-trip): excepción opcional de
        // "quién asume el ajuste por el dólar" para este operador. Este modal NO expone
        // un control para editarlo (esa UI vive en la ficha del operador, solapa
        // "Datos" — ver SupplierAccountPage.jsx); se preserva acá SOLO para que el PUT
        // de este modal no la pise con `null` por accidente (el PUT asigna este campo
        // SIEMPRE, a diferencia de defaultCurrency/defaultPaymentTermDays).
        treasuryFxAssumedByOverride: null,
        // Configuracion de multas de cancelacion (2026-07-14, gate de frontend — mismo
        // round-trip que el campo de arriba): "¿Suele cobrar multa cuando se anula?" se
        // edita SOLO en SupplierAccountPage.jsx (Pieza 1 de
        // docs/ux/2026-07-14-config-multas-proveedor.md, que explícitamente no toca este
        // modal viejo). Se preserva igual acá porque el PUT del operador asigna SIEMPRE
        // este campo — si este modal lo omitiera, guardar una edición cualquiera desde
        // acá (nombre, teléfono, etc.) borraría en silencio la configuración ya cargada,
        // volviéndola a "no se sabe" sin que nadie lo pidiera.
        penaltyBehavior: SUPPLIER_PENALTY_BEHAVIOR.Unknown,
        invoicingMode: 0,
    });

    useEffect(() => {
        if (supplier) {
            setFormData({
                name: supplier.name || "",
                taxId: supplier.taxId || "",
                taxCondition: supplier.taxCondition || "",
                address: supplier.address || "",
                contactName: supplier.contactName || "",
                email: supplier.email || "",
                phone: supplier.phone || "",
                isActive: supplier.isActive ?? true,
                currentBalance: supplier.currentBalance || 0,
                // Moneda por defecto: fallback ARS para operadores creados antes de que el campo existiera.
                defaultCurrency: supplier.defaultCurrency || "ARS",
                // Round-trip: preservamos el plazo pactado aunque no se muestre en este form.
                defaultPaymentTermDays: supplier.defaultPaymentTermDays ?? null,
                // Round-trip (ver comentario del estado inicial): preservamos la excepción
                // real del operador aunque este modal no la muestre ni la edite.
                treasuryFxAssumedByOverride: supplier.treasuryFxAssumedByOverride ?? null,
                // Round-trip (ver comentario del estado inicial): preservamos el
                // comportamiento con multas ya configurado, aunque este modal no lo
                // muestre. valorSelectDesdePenaltyBehavior normaliza cualquier fila vieja
                // que todavía no traiga el campo al default "no se sabe" (nunca inventa
                // una configuración que el operador no tiene).
                penaltyBehavior: valorSelectDesdePenaltyBehavior(supplier.penaltyBehavior),
                invoicingMode: supplier.invoicingMode ?? 0,
            });
        } else {
            setFormData({
                name: "",
                contactName: "",
                taxId: "",
                taxCondition: "",
                address: "",
                email: "",
                phone: "",
                isActive: true,
                currentBalance: 0,
                defaultCurrency: "ARS",
                defaultPaymentTermDays: null,
                treasuryFxAssumedByOverride: null,
                penaltyBehavior: SUPPLIER_PENALTY_BEHAVIOR.Unknown,
                invoicingMode: 0,
            });
        }
    }, [supplier, isOpen]);

    const handleSubmit = (e) => {
        e.preventDefault();
        onSave(formData, getPublicId(supplier));
    };

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm p-4 animate-in fade-in zoom-in duration-200">
            <div className="w-full max-w-lg rounded-2xl bg-white p-6 shadow-2xl dark:bg-slate-900 border border-slate-200 dark:border-slate-800">
                <div className="flex items-center justify-between mb-6">
                    <div>
                        <h3 className="text-xl font-bold text-slate-900 dark:text-white">
                            {supplier ? "Editar Proveedor" : "Nuevo Proveedor"}
                        </h3>
                        <p className="text-sm text-muted-foreground mt-1">
                            {supplier ? "Modifique los datos del proveedor" : "Ingrese los datos para dar de alta un nuevo proveedor"}
                        </p>
                    </div>
                    {supplier && (
                        <Badge variant={formData.isActive ? "success" : "secondary"}>
                            {formData.isActive ? "Activo" : "Inactivo"}
                        </Badge>
                    )}
                </div>

                <form onSubmit={handleSubmit} className="space-y-5">
                    <div className="grid gap-4 sm:grid-cols-2">
                        <div className="space-y-2 sm:col-span-2">
                            <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Razón Social *</label>
                            <input
                                type="text"
                                required
                                placeholder="Ej: Despegar Argentina S.A."
                                value={formData.name}
                                onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                                className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                            />
                        </div>

                        <div className="space-y-2">
                            <label className="text-sm font-medium text-slate-700 dark:text-slate-300">CUIT</label>
                            <input
                                type="text"
                                placeholder="20-12345678-9"
                                value={formData.taxId}
                                onChange={(e) => setFormData({ ...formData, taxId: e.target.value })}
                                className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                            />
                        </div>

                        <div className="space-y-2">
                            <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Condición Fiscal</label>
                            <select
                                value={formData.taxCondition}
                                onChange={(e) => setFormData({ ...formData, taxCondition: e.target.value })}
                                className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                            >
                                <option value="">Seleccionar...</option>
                                <option value="IVA_RESP_INSCRIPTO">Resp. Inscripto</option>
                                <option value="MONOTRIBUTISTA">Monotributista</option>
                                <option value="IVA_EXENTO">Exento</option>
                                <option value="CONSUMIDOR_FINAL">Cons. Final</option>
                            </select>
                        </div>

                        {/* Moneda por defecto: define en qué carril de cuenta corriente opera este proveedor.
                            ARS y USD son extractos separados; no se mezclan nunca. */}
                        <div className="space-y-2 sm:col-span-2">
                            <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Moneda por defecto</label>
                            <select
                                value={formData.defaultCurrency}
                                onChange={(e) => setFormData({ ...formData, defaultCurrency: e.target.value })}
                                className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                                data-testid="supplier-form-defaultCurrency"
                            >
                                {CURRENCY_OPTIONS.map((opt) => (
                                    <option key={opt.value} value={opt.value}>
                                        {opt.label}
                                    </option>
                                ))}
                            </select>
                        </div>

                        <div className="space-y-2">
                            <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Contacto</label>
                            <input
                                type="text"
                                placeholder="Nombre contacto"
                                value={formData.contactName}
                                onChange={(e) => setFormData({ ...formData, contactName: e.target.value })}
                                className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                            />
                        </div>

                        <div className="space-y-2">
                            <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Teléfono</label>
                            <input
                                type="text"
                                placeholder="+54 11 ..."
                                value={formData.phone}
                                onChange={(e) => setFormData({ ...formData, phone: e.target.value })}
                                className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                            />
                        </div>

                        <div className="space-y-2 sm:col-span-2">
                            <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Email</label>
                            <input
                                type="email"
                                placeholder="contacto@proveedor.com"
                                value={formData.email}
                                onChange={(e) => setFormData({ ...formData, email: e.target.value })}
                                className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                            />
                        </div>

                        {supplier && (
                            <div className="sm:col-span-2 rounded-lg border bg-slate-50 dark:bg-slate-900/50 p-3 flex items-center justify-between">
                                <div className="flex items-center gap-2">
                                    <Wallet className="h-4 w-4 text-slate-500" />
                                    <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Saldo Actual</span>
                                </div>
                                {(() => {
                                    // Mismos 3 casos que SupplierTable (fix del reviewer, 2026-07-22):
                                    // supplierBalanceLines() devuelve [] tanto para "saldo en cero" como
                                    // para "sin permiso para ver montos" (amountsVisible === false) — hay
                                    // que distinguirlos ACÁ, antes de preguntar por la lista, porque si no
                                    // un usuario sin permiso ve "Sin saldo" (afirma un hecho falso: no es
                                    // que no deba nada, es que no le mostramos cuánto debe).
                                    if (supplier.amountsVisible === false) {
                                        return <span className="text-sm text-slate-400" title="Sin permiso para ver montos">—</span>;
                                    }
                                    const balanceLines = supplierBalanceLines(supplier);
                                    if (balanceLines.length === 0) {
                                        return <span className="text-sm text-slate-500">Sin saldo</span>;
                                    }
                                    return (
                                        <div className="space-y-0.5 text-right font-mono font-bold">
                                            {balanceLines.map((line) => (
                                                <div
                                                    key={line.currency}
                                                    className={line.balance > 0 ? "text-rose-600" : "text-emerald-600"}
                                                >
                                                    {formatCurrency(line.balance, line.currency)}
                                                </div>
                                            ))}
                                        </div>
                                    );
                                })()}
                            </div>
                        )}
                    </div>

                    <div className="flex gap-3 pt-4 border-t border-slate-100 dark:border-slate-800">
                        <button
                            type="button"
                            onClick={onClose}
                            className="flex-1 rounded-lg border bg-white px-4 py-2.5 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-900 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800 transition-colors"
                        >
                            Cancelar
                        </button>
                        <button
                            type="submit"
                            className="flex-1 rounded-lg bg-indigo-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-indigo-700 shadow-lg shadow-indigo-500/25 transition-all"
                        >
                            {supplier ? "Guardar Cambios" : "Crear Proveedor"}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
}
