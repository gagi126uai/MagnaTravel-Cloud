import { Link } from "react-router-dom";
import {
  AlertCircle,
  ArrowRight,
  ArrowLeftRight,
  Banknote,
  CalendarClock,
  FileText,
  Loader2,
  ShieldAlert,
  TrendingDown,
  TrendingUp,
} from "lucide-react";
import { useFinanceHome } from "../hooks/useFinanceHome";
import { formatCurrency, formatDate } from "../lib/financeUtils";
import { getPublicId } from "../../../lib/publicIds";

function HomeCard({ title, description, icon: Icon, accentClass, metrics, ctaTo, ctaLabel }) {
  return (
    <div className="rounded-3xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 shadow-sm overflow-hidden">
      <div className="p-6 space-y-5">
        <div className="flex items-start justify-between gap-4">
          <div>
            <div className="text-lg font-semibold text-slate-900 dark:text-white">{title}</div>
            <div className="text-sm text-slate-500 dark:text-slate-400 mt-1">{description}</div>
          </div>
          <div className={`p-3 rounded-2xl ${accentClass}`}>
            <Icon className="w-5 h-5" />
          </div>
        </div>

        <div className="grid grid-cols-1 gap-3">
          {metrics.map((metric) => (
            <div
              key={metric.label}
              className="rounded-2xl border border-slate-100 dark:border-slate-800 bg-slate-50/70 dark:bg-slate-950/40 px-4 py-3"
            >
              <div className="text-[11px] uppercase tracking-wider font-semibold text-slate-400 mb-1">
                {metric.label}
              </div>
              <div className="text-xl font-light text-slate-900 dark:text-white">
                {metric.isCount ? Number(metric.value || 0) : formatCurrency(metric.value)}
              </div>
            </div>
          ))}
        </div>

        <Link
          to={ctaTo}
          className="inline-flex items-center gap-2 text-sm font-medium text-indigo-600 hover:text-indigo-700"
        >
          {ctaLabel}
          <ArrowRight className="w-4 h-4" />
        </Link>
      </div>
    </div>
  );
}

function AlertList({ title, items, emptyText, renderItem, icon: Icon }) {
  return (
    <div className="rounded-3xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 shadow-sm overflow-hidden">
      <div className="px-6 py-5 border-b border-slate-100 dark:border-slate-800 flex items-center gap-3">
        <div className="p-2 rounded-xl bg-slate-100 dark:bg-slate-800 text-slate-500">
          <Icon className="w-4 h-4" />
        </div>
        <div>
          <div className="font-semibold text-slate-900 dark:text-white">{title}</div>
          <div className="text-sm text-slate-500 dark:text-slate-400">Prioridades del modulo</div>
        </div>
      </div>

      <div className="p-6 space-y-3">
        {items.length === 0 ? (
          <div className="text-sm text-slate-500 dark:text-slate-400">{emptyText}</div>
        ) : (
          items.map(renderItem)
        )}
      </div>
    </div>
  );
}

export default function PaymentsHomePage() {
  const { loading, collectionsSummary, cashSummary, invoicingSummary, alerts } = useFinanceHome();

  if (loading) {
    return (
      <div className="flex justify-center items-center h-64 text-slate-400">
        <Loader2 className="w-8 h-8 animate-spin" />
      </div>
    );
  }

  return (
    <div className="space-y-8">
      <div className="grid grid-cols-1 xl:grid-cols-3 gap-6">
        <HomeCard
          title="Cobranzas"
          description="Seguimiento operativo de pagos de reservas y alertas por deuda."
          icon={Banknote}
          accentClass="bg-amber-50 text-amber-600 dark:bg-amber-950/30 dark:text-amber-300"
          metrics={[
            { label: "Saldo pendiente de cobro", value: collectionsSummary?.pendingAmount || 0 },
            { label: "Cobrado este mes", value: collectionsSummary?.collectedThisMonth || 0 },
            { label: "Reservas urgentes", value: collectionsSummary?.urgentReservationsCount || 0, isCount: true },
          ]}
          ctaTo="/payments/collections"
          ctaLabel="Ver cobranzas"
        />

        <HomeCard
          title="Caja"
          description="Movimientos reales de dinero: ingresos, egresos y ajustes manuales."
          icon={ArrowLeftRight}
          accentClass="bg-emerald-50 text-emerald-600 dark:bg-emerald-950/30 dark:text-emerald-300"
          metrics={[
            { label: "Ingresos del mes", value: cashSummary?.cashInThisMonth || 0 },
            { label: "Egresos del mes", value: cashSummary?.cashOutThisMonth || 0 },
            { label: "Resultado de caja del mes", value: cashSummary?.netCashThisMonth || 0 },
          ]}
          ctaTo="/payments/cash"
          ctaLabel="Ver caja"
        />

        <HomeCard
          title="Facturacion"
          description="Estado fiscal AFIP: que esta listo, que esta bloqueado y que ya se emitio."
          icon={FileText}
          accentClass="bg-indigo-50 text-indigo-600 dark:bg-indigo-950/30 dark:text-indigo-300"
          metrics={[
            { label: "Listo para facturar", value: invoicingSummary?.readyAmount || 0 },
            { label: "Facturado en AFIP este mes", value: invoicingSummary?.invoicedThisMonth || 0 },
            { label: "Bloqueadas por deuda", value: invoicingSummary?.blockedCount || 0, isCount: true },
          ]}
          ctaTo="/payments/invoicing"
          ctaLabel="Ver facturacion"
        />
      </div>

      <div className="grid grid-cols-1 xl:grid-cols-2 gap-6">
        <AlertList
          title="Reservas con salida proxima y deuda"
          items={alerts?.UrgentTrips || []}
          emptyText="No hay reservas urgentes con deuda al momento."
          icon={CalendarClock}
          renderItem={(trip) => (
            <Link
              key={`trip-${getPublicId(trip)}`}
              to={`/reservas/${getPublicId(trip)}`}
              className="flex items-start justify-between gap-4 rounded-2xl border border-slate-100 dark:border-slate-800 px-4 py-3 hover:bg-slate-50 dark:hover:bg-slate-800/40 transition-colors"
            >
              <div>
                <div className="font-semibold text-slate-900 dark:text-white">{trip.numeroReserva}</div>
                <div className="text-sm text-slate-500 dark:text-slate-400">{trip.payerName || trip.name}</div>
                <div className="text-xs text-slate-400 mt-1">Salida {formatDate(trip.startDate)}</div>
              </div>
              <div className="text-right">
                <div className="inline-flex items-center gap-1 text-xs font-semibold text-rose-600 dark:text-rose-400">
                  <AlertCircle className="w-3.5 h-3.5" />
                  Urgente
                </div>
                <div className="text-sm font-semibold text-rose-600 dark:text-rose-400 mt-1">
                  {formatCurrency(trip.balance)}
                </div>
              </div>
            </Link>
          )}
        />

        <AlertList
          title="Deuda con proveedores"
          items={alerts?.SupplierDebts || []}
          emptyText="No hay deudas relevantes con proveedores."
          icon={ShieldAlert}
          renderItem={(supplier) => (
            <Link
              key={`supplier-${getPublicId(supplier)}`}
              to={`/suppliers/${getPublicId(supplier)}/account`}
              className="flex items-start justify-between gap-4 rounded-2xl border border-slate-100 dark:border-slate-800 px-4 py-3 hover:bg-slate-50 dark:hover:bg-slate-800/40 transition-colors"
            >
              <div>
                <div className="font-semibold text-slate-900 dark:text-white">{supplier.name}</div>
                <div className="text-sm text-slate-500 dark:text-slate-400">
                  {supplier.phone || "Sin telefono cargado"}
                </div>
              </div>
              <div className="text-right text-sm font-semibold text-rose-600 dark:text-rose-400">
                {formatCurrency(supplier.currentBalance)}
              </div>
            </Link>
          )}
        />
      </div>

      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <div className="rounded-2xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 px-5 py-4 shadow-sm">
          <div className="flex items-center gap-3 mb-2 text-slate-500">
            <TrendingUp className="w-4 h-4" />
            <span className="text-sm font-medium">Lectura operativa</span>
          </div>
          <div className="text-sm text-slate-500 dark:text-slate-400">
            Cobranzas responde que reserva hay que cobrar y cual esta en riesgo por fecha.
          </div>
        </div>
        <div className="rounded-2xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 px-5 py-4 shadow-sm">
          <div className="flex items-center gap-3 mb-2 text-slate-500">
            <TrendingDown className="w-4 h-4" />
            <span className="text-sm font-medium">Lectura contable</span>
          </div>
          <div className="text-sm text-slate-500 dark:text-slate-400">
            Caja muestra solo dinero que entro o salio realmente, no deuda comercial.
          </div>
        </div>
        <div className="rounded-2xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 px-5 py-4 shadow-sm">
          <div className="flex items-center gap-3 mb-2 text-slate-500">
            <FileText className="w-4 h-4" />
            <span className="text-sm font-medium">Lectura fiscal</span>
          </div>
          <div className="text-sm text-slate-500 dark:text-slate-400">
            Facturacion separa lo listo para AFIP, lo bloqueado por deuda y lo ya emitido.
          </div>
        </div>
      </div>
    </div>
  );
}
