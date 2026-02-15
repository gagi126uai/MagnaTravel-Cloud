import { useEffect, useState } from "react";
import { api } from "../api";
import { showError } from "../alerts";
import { formatCurrency, formatDate } from "../lib/utils";
import {
  TrendingUp,
  DollarSign,
  ArrowDownRight,
  ArrowUpRight,
  Percent,
  Building2,
  Users,
  Calendar,
  FileText,
  Download,
  BarChart3,
  Wallet,
  CreditCard,
  AlertCircle,
  Loader2
} from "lucide-react";
import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
  Legend
} from "recharts";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle
} from "../components/ui/card";
import { ReportsSkeleton } from "../components/ui/skeleton";

export default function ReportsPage() {
  const [report, setReport] = useState(null);
  const [loading, setLoading] = useState(true);

  // Date range — default: first day of current month to today
  const today = new Date();
  const firstOfMonth = new Date(today.getFullYear(), today.getMonth(), 1);
  const [dateFrom, setDateFrom] = useState(firstOfMonth.toISOString().split("T")[0]);
  const [dateTo, setDateTo] = useState(today.toISOString().split("T")[0]);

  // Quick presets
  const [activePreset, setActivePreset] = useState("month");

  const applyPreset = (preset) => {
    setActivePreset(preset);
    const now = new Date();
    let from, to;

    switch (preset) {
      case "month":
        from = new Date(now.getFullYear(), now.getMonth(), 1);
        to = now;
        break;
      case "lastMonth": {
        from = new Date(now.getFullYear(), now.getMonth() - 1, 1);
        to = new Date(now.getFullYear(), now.getMonth(), 0);
        break;
      }
      case "quarter":
        from = new Date(now.getFullYear(), Math.floor(now.getMonth() / 3) * 3, 1);
        to = now;
        break;
      case "year":
        from = new Date(now.getFullYear(), 0, 1);
        to = now;
        break;
      case "all":
        from = new Date(2020, 0, 1);
        to = now;
        break;
      default:
        return;
    }

    setDateFrom(from.toISOString().split("T")[0]);
    setDateTo(to.toISOString().split("T")[0]);
  };

  useEffect(() => {
    loadReport();
  }, [dateFrom, dateTo]);

  const loadReport = async () => {
    try {
      setLoading(true);
      const data = await api.get(`/reports/detailed?from=${dateFrom}&to=${dateTo}`);
      setReport(data);
    } catch (error) {
      console.error("Error loading report:", error);
      showError("No se pudieron cargar los reportes.");
    } finally {
      setLoading(false);
    }
  };

  if (loading && !report) {
    return <ReportsSkeleton />;
  }

  if (!report) {
    return (
      <div className="text-center py-12">
        <p className="text-muted-foreground">No se pudieron cargar los reportes.</p>
      </div>
    );
  }

  const s = report.summary;
  const cashFlow = s.customerPayments - s.supplierPayments;

  return (
    <div className="space-y-6 animate-in fade-in duration-500">
      {/* Header */}
      <div className="flex flex-col md:flex-row justify-between items-start md:items-center gap-4">
        <div>
          <h2 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white flex items-center gap-2">
            <BarChart3 className="h-6 w-6 text-indigo-600" />
            Reportes
          </h2>
          <p className="text-sm text-muted-foreground mt-1">
            Análisis financiero y operativo de tu agencia
          </p>
        </div>
      </div>

      {/* Date Filter Bar */}
      <div className="flex flex-col sm:flex-row gap-4 items-start sm:items-center justify-between bg-white dark:bg-slate-900/50 p-4 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm">
        <div className="flex flex-wrap gap-1.5">
          {[
            { key: "month", label: "Este Mes" },
            { key: "lastMonth", label: "Mes Anterior" },
            { key: "quarter", label: "Trimestre" },
            { key: "year", label: "Año" },
            { key: "all", label: "Todo" },
          ].map((p) => (
            <button
              key={p.key}
              onClick={() => applyPreset(p.key)}
              className={`px-3 py-1.5 text-xs font-medium rounded-lg transition-all ${activePreset === p.key
                ? "bg-indigo-600 text-white shadow-sm"
                : "bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700"
                }`}
            >
              {p.label}
            </button>
          ))}
        </div>
        <div className="flex items-center gap-2">
          <Calendar className="h-4 w-4 text-slate-400" />
          <input
            type="date"
            value={dateFrom}
            onChange={(e) => { setDateFrom(e.target.value); setActivePreset(""); }}
            className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-800 dark:border-slate-700 dark:text-white"
          />
          <span className="text-slate-400 text-sm">a</span>
          <input
            type="date"
            value={dateTo}
            onChange={(e) => { setDateTo(e.target.value); setActivePreset(""); }}
            className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-800 dark:border-slate-700 dark:text-white"
          />
          {loading && <Loader2 className="h-4 w-4 animate-spin text-indigo-500" />}
        </div>
      </div>

      {/* KPI Cards */}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
        <KpiCard
          title="Ventas"
          value={formatCurrency(s.totalSales)}
          subtitle={`${s.filesCount} expedientes`}
          icon={TrendingUp}
          color="indigo"
        />
        <KpiCard
          title="Margen Bruto"
          value={formatCurrency(s.grossMargin)}
          subtitle={`${s.marginPercent}% margen`}
          icon={Percent}
          color="emerald"
        />
        <KpiCard
          title="Cobros Clientes"
          value={formatCurrency(s.customerPayments)}
          subtitle="Ingresos de caja"
          icon={Wallet}
          color="blue"
        />
        <KpiCard
          title="Flujo de Caja"
          value={formatCurrency(cashFlow)}
          subtitle={cashFlow >= 0 ? "Superávit" : "Déficit"}
          icon={cashFlow >= 0 ? ArrowUpRight : ArrowDownRight}
          color={cashFlow >= 0 ? "emerald" : "rose"}
        />
      </div>

      {/* Secondary KPIs */}
      <div className="grid grid-cols-2 md:grid-cols-3 gap-4">
        <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
          <div className="flex items-center gap-2 text-xs text-slate-500 mb-2">
            <DollarSign className="h-3.5 w-3.5" />
            Costos Totales
          </div>
          <div className="text-lg font-bold text-slate-700 dark:text-slate-300">{formatCurrency(s.totalCosts)}</div>
        </div>
        <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
          <div className="flex items-center gap-2 text-xs text-slate-500 mb-2">
            <CreditCard className="h-3.5 w-3.5" />
            Pagos a Proveedores
          </div>
          <div className="text-lg font-bold text-slate-700 dark:text-slate-300">{formatCurrency(s.supplierPayments)}</div>
        </div>
        <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50 col-span-2 md:col-span-1">
          <div className="flex items-center gap-2 text-xs text-slate-500 mb-2">
            <FileText className="h-3.5 w-3.5" />
            Expedientes del Período
          </div>
          <div className="text-lg font-bold text-slate-700 dark:text-slate-300">{s.filesCount}</div>
        </div>
      </div>

      {/* Monthly Chart */}
      {report.monthlyBreakdown?.length > 0 && (
        <Card className="shadow-sm">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <BarChart3 className="h-5 w-5 text-slate-500" />
              Evolución Mensual
            </CardTitle>
            <CardDescription>Ventas vs Costos por mes en el período seleccionado</CardDescription>
          </CardHeader>
          <CardContent className="pl-0">
            <div className="h-[300px] w-full">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={report.monthlyBreakdown} margin={{ top: 10, right: 30, left: 0, bottom: 0 }}>
                  <CartesianGrid strokeDasharray="3 3" vertical={false} stroke="#e2e8f0" />
                  <XAxis dataKey="month" stroke="#64748b" fontSize={12} tickLine={false} axisLine={false} />
                  <YAxis stroke="#64748b" fontSize={12} tickLine={false} axisLine={false}
                    tickFormatter={(v) => v >= 1000 ? `$${(v / 1000).toFixed(0)}k` : `$${v}`} />
                  <Tooltip
                    cursor={{ fill: '#f1f5f9' }}
                    contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }}
                    formatter={(value) => [formatCurrency(value), undefined]}
                  />
                  <Legend wrapperStyle={{ paddingTop: '20px' }} />
                  <Bar dataKey="sales" name="Ventas" fill="#6366f1" radius={[4, 4, 0, 0]} barSize={30} />
                  <Bar dataKey="costs" name="Costos" fill="#94a3b8" radius={[4, 4, 0, 0]} barSize={30} />
                  <Bar dataKey="margin" name="Margen" fill="#10b981" radius={[4, 4, 0, 0]} barSize={30} />
                </BarChart>
              </ResponsiveContainer>
            </div>
          </CardContent>
        </Card>
      )}

      {/* Tables Section */}
      <div className="grid gap-6 lg:grid-cols-2">
        {/* Supplier Debts */}
        <Card className="shadow-sm">
          <CardHeader>
            <CardTitle className="flex items-center gap-2 text-rose-600 dark:text-rose-400">
              <Building2 className="h-5 w-5" />
              Deuda con Proveedores
            </CardTitle>
            <CardDescription>Saldo actual por proveedor (en tiempo real)</CardDescription>
          </CardHeader>
          <CardContent>
            {report.supplierDebts?.length > 0 ? (
              <div className="space-y-2 max-h-[350px] overflow-y-auto pr-1">
                {report.supplierDebts.map((sup) => (
                  <div key={sup.id} className="flex items-center justify-between p-3 rounded-lg bg-slate-50 dark:bg-slate-800/50 border border-slate-100 dark:border-slate-800 hover:border-slate-200 dark:hover:border-slate-700 transition-colors">
                    <div className="flex items-center gap-3">
                      <div className="h-8 w-8 rounded-full bg-rose-100 dark:bg-rose-900/30 flex items-center justify-center">
                        <Building2 className="h-4 w-4 text-rose-600 dark:text-rose-400" />
                      </div>
                      <span className="font-medium text-sm text-slate-700 dark:text-slate-300">{sup.name}</span>
                    </div>
                    <span className="font-mono font-bold text-rose-600 dark:text-rose-400 text-sm">
                      {formatCurrency(sup.currentBalance)}
                    </span>
                  </div>
                ))}
                <div className="pt-2 border-t border-slate-200 dark:border-slate-700 flex justify-between px-3">
                  <span className="text-sm font-semibold text-slate-600 dark:text-slate-400">Total</span>
                  <span className="font-mono font-bold text-rose-700 dark:text-rose-400">
                    {formatCurrency(report.supplierDebts.reduce((sum, s) => sum + s.currentBalance, 0))}
                  </span>
                </div>
              </div>
            ) : (
              <EmptyState message="Sin deuda con proveedores" />
            )}
          </CardContent>
        </Card>

        {/* Top Customers */}
        <Card className="shadow-sm">
          <CardHeader>
            <CardTitle className="flex items-center gap-2 text-indigo-600 dark:text-indigo-400">
              <Users className="h-5 w-5" />
              Top Clientes
            </CardTitle>
            <CardDescription>Ranking por volumen de ventas en el período</CardDescription>
          </CardHeader>
          <CardContent>
            {report.topCustomers?.length > 0 ? (
              <div className="space-y-2 max-h-[350px] overflow-y-auto pr-1">
                {report.topCustomers.map((cust, i) => (
                  <div key={i} className="flex items-center justify-between p-3 rounded-lg bg-slate-50 dark:bg-slate-800/50 border border-slate-100 dark:border-slate-800 hover:border-slate-200 dark:hover:border-slate-700 transition-colors">
                    <div className="flex items-center gap-3">
                      <div className={`h-7 w-7 rounded-full flex items-center justify-center text-xs font-bold ${i === 0 ? 'bg-amber-100 text-amber-700' :
                        i === 1 ? 'bg-slate-200 text-slate-600' :
                          i === 2 ? 'bg-orange-100 text-orange-700' :
                            'bg-slate-100 text-slate-500'
                        }`}>
                        {i + 1}
                      </div>
                      <div>
                        <span className="font-medium text-sm text-slate-700 dark:text-slate-300">{cust.name}</span>
                        <div className="text-[10px] text-slate-400">{cust.fileCount} exp.</div>
                      </div>
                    </div>
                    <div className="text-right">
                      <div className="font-mono font-bold text-sm text-indigo-600 dark:text-indigo-400">
                        {formatCurrency(cust.totalSale)}
                      </div>
                      {cust.pendingBalance > 0 && (
                        <div className="flex items-center gap-1 justify-end">
                          <AlertCircle className="h-3 w-3 text-rose-500" />
                          <span className="text-[10px] text-rose-500 font-medium">
                            Debe {formatCurrency(cust.pendingBalance)}
                          </span>
                        </div>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            ) : (
              <EmptyState message="Sin datos de clientes en este período" />
            )}
          </CardContent>
        </Card>
      </div>
    </div>
  );
}

function KpiCard({ title, value, subtitle, icon: Icon, color }) {
  const colorMap = {
    indigo: { bg: 'bg-indigo-50 dark:bg-indigo-900/10', text: 'text-indigo-600 dark:text-indigo-400' },
    emerald: { bg: 'bg-emerald-50 dark:bg-emerald-900/10', text: 'text-emerald-600 dark:text-emerald-400' },
    blue: { bg: 'bg-blue-50 dark:bg-blue-900/10', text: 'text-blue-600 dark:text-blue-400' },
    rose: { bg: 'bg-rose-50 dark:bg-rose-900/10', text: 'text-rose-600 dark:text-rose-400' },
  };
  const c = colorMap[color] || colorMap.indigo;

  return (
    <Card className={`border-none shadow-sm ${c.bg} transition-all hover:scale-[1.02]`}>
      <CardContent className="p-5">
        <div className="flex items-center justify-between">
          <p className={`text-xs font-medium ${c.text} opacity-80 uppercase tracking-wider`}>{title}</p>
          <Icon className={`h-4 w-4 ${c.text}`} />
        </div>
        <div className="mt-2">
          <span className={`text-2xl font-bold ${c.text}`}>{value}</span>
        </div>
        {subtitle && (
          <p className={`text-xs ${c.text} mt-1 opacity-70`}>{subtitle}</p>
        )}
      </CardContent>
    </Card>
  );
}

function EmptyState({ message }) {
  return (
    <div className="text-center py-8 text-muted-foreground">
      <p className="text-sm">{message}</p>
    </div>
  );
}
