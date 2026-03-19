import React, { useState } from 'react';
import { User, Users, Calendar, AlertCircle, CheckCircle2, FolderOpen, MessageCircle } from "lucide-react";
import { Button } from "../../../components/ui/button";
import { ReservaStatusBadge } from "./ReservaStatusBadge";
import { formatCurrency, formatDate } from "../../../lib/utils";

export function ReservaMobileList({ reservas, onRowClick }) {
    const [visibleCount, setVisibleCount] = useState(50);
    const visibleReservas = reservas.slice(0, visibleCount);

    const handleLoadMore = () => {
        setVisibleCount(prev => prev + 50);
    };

    if (reservas.length === 0) {
        return (
            <div className="text-center py-12 bg-slate-50 dark:bg-slate-900 rounded-xl border border-dashed border-slate-200 dark:border-slate-800">
                <p className="text-muted-foreground text-sm">No se encontraron reservas.</p>
            </div>
        );
    }

    return (
        <div className="space-y-3">
            {visibleReservas.map((reserva) => {
                const hasPendingBalance = reserva.balance > 0;
                const isPaid = reserva.totalSale > 0 && reserva.balance <= 0;
                return (
                    <div
                        key={reserva.id}
                        onClick={() => onRowClick(reserva.id)}
                        className="bg-white dark:bg-slate-900 rounded-xl p-4 border border-slate-200 dark:border-slate-800 shadow-sm active:scale-[0.98] transition-transform"
                    >
                        <div className="flex justify-between items-start mb-3">
                            <div className="flex items-center gap-3">
                                <div className={`h-10 w-10 rounded-full flex items-center justify-center shrink-0 ${hasPendingBalance
                                    ? 'bg-rose-50 text-rose-600 dark:bg-rose-900/20 dark:text-rose-400'
                                    : isPaid
                                        ? 'bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-400'
                                        : 'bg-indigo-50 text-indigo-600 dark:bg-indigo-900/20 dark:text-indigo-400'
                                    }`}>
                                    {hasPendingBalance
                                        ? <AlertCircle className="h-5 w-5" />
                                        : isPaid
                                            ? <CheckCircle2 className="h-5 w-5" />
                                            : <FolderOpen className="h-5 w-5" />
                                    }
                                </div>
                                <div>
                                    <div className="font-semibold text-slate-900 dark:text-white leading-tight">{reserva.name}</div>
                                    <div className="flex items-center gap-2 mt-1">
                                        <span className="text-xs text-slate-500 font-mono">#{reserva.numeroReserva}</span>
                                    </div>
                                </div>
                            </div>
                            <ReservaStatusBadge status={reserva.status} />
                        </div>

                        <div className="grid grid-cols-2 gap-y-2 gap-x-4 text-sm mb-3">
                            <div className="flex items-center gap-2 text-slate-600 dark:text-slate-400 font-medium">
                                <User className="h-3.5 w-3.5 opacity-70" />
                                <span className="truncate">{reserva.customerName || "Sin Asignar"}</span>
                            </div>
                            <div className="relative">
                                {reserva.startDate ? (
                                    <div className="flex flex-col">
                                        <div className="flex items-center gap-1.5 text-xs font-medium">
                                            <Calendar className="h-3.5 w-3.5 opacity-60 text-indigo-500" />
                                            {formatDate(reserva.startDate)}
                                        </div>
                                    </div>
                                ) : (
                                    <span className="text-xs text-slate-400">-</span>
                                )}
                            </div>
                        </div>

                        <div className="flex justify-between items-center pt-3 border-t border-slate-100 dark:border-slate-800">
                            <div className="text-xs text-slate-500">
                                Venta: <span className="font-medium text-slate-900 dark:text-slate-200">{formatCurrency(reserva.totalSale)}</span>
                            </div>
                            <div className="flex items-center gap-2">
                                <span className={`text-sm font-bold ${hasPendingBalance ? 'text-rose-600 dark:text-rose-400' : 'text-emerald-600 dark:text-emerald-400'}`}>
                                    {hasPendingBalance ? "Saldo: " + formatCurrency(reserva.balance) : "Pagado"}
                                </span>
                            </div>
                        </div>
                    </div>
                );
            })}
            
            {reservas.length > visibleCount && (
                <div className="pt-2 pb-4 text-center">
                    <Button 
                        variant="outline" 
                        onClick={handleLoadMore}
                        className="text-sm font-semibold text-slate-600 dark:text-slate-300 w-full"
                    >
                        Cargar más resultados ({reservas.length - visibleCount} restantes)
                    </Button>
                </div>
            )}
        </div>
    );
}
