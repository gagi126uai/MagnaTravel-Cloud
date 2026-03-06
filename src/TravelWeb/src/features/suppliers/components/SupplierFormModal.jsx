import React, { useState, useEffect } from 'react';
import { Wallet } from "lucide-react";
import { Badge } from "../../../components/ui/badge";
import { formatCurrency } from "../../../lib/utils";

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
        currentBalance: 0
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
                currentBalance: supplier.currentBalance || 0
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
                currentBalance: 0
            });
        }
    }, [supplier, isOpen]);

    const handleSubmit = (e) => {
        e.preventDefault();
        onSave(formData, supplier?.id);
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
                                <span className={`font-mono font-bold ${supplier.currentBalance > 0 ? "text-rose-600" : "text-emerald-600"}`}>
                                    {formatCurrency(supplier.currentBalance)}
                                </span>
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
