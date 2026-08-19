import { useCallback, useEffect, useState } from "react";
import { api } from "../api";
import { hasPermission, isAdmin } from "../auth";
import { DashboardSkeleton } from "../components/ui/skeleton";
import { calcularVisibilidadDashboard } from "../features/dashboard/lib/dashboardVisibility";
import { DashboardHeader } from "../features/dashboard/components/DashboardHeader";
import { UpcomingTripsCard } from "../features/dashboard/components/UpcomingTripsCard";
import { PendingCollectionsCard } from "../features/dashboard/components/PendingCollectionsCard";
import { OpenBudgetsAndCrmCard } from "../features/dashboard/components/OpenBudgetsAndCrmCard";
import { MoneyKpiGrid } from "../features/dashboard/components/MoneyKpiGrid";
import { CashflowRhythmCard } from "../features/dashboard/components/CashflowRhythmCard";
import { ReportsLinkCard } from "../features/dashboard/components/ReportsLinkCard";

/**
 * Dashboard "Inicio" — Opción C "Panorama ERP" (spec firmada 2026-08-18,
 * `docs/ux/2026-08-18-spec-dashboard-y-cuentas-corrientes.md`).
 *
 * R1 de la spec: reemplaza al viejo router por `isAdmin` (que mandaba a
 * `AdminDashboard.jsx` o `AgentDashboard.jsx`, dos pantallas separadas y sin
 * ningún camino intermedio para un colaborador con permisos puntuales). Ahora
 * hay UN SOLO dashboard: arma su layout mirando `hasPermission`/`isAdmin`
 * pieza por pieza (ver `dashboardVisibility.js`), así que un colaborador con
 * `cobranzas.see_cost` ve exactamente lo mismo que un dueño, sea o no Admin.
 *
 * Dos columnas (spec 1.3): TRABAJO a la izquierda (lo que hay que hacer hoy),
 * PLATA a la derecha (cómo viene la agencia de números). En mobile se apilan
 * en ese mismo orden (spec 1.7) — el layout de abajo ya las pone en ese orden
 * en el HTML, así que no hace falta ningún truco de `order-*`.
 */
export default function DashboardPage() {
  const [dashboard, setDashboard] = useState(null);
  const [cashflow, setCashflow] = useState(null);
  const [loading, setLoading] = useState(true);

  // hasPermission/isAdmin leen el estado de auth global (auth.js) en cada
  // render — no hace falta un hook propio: App.jsx ya está suscripto a ese
  // estado con useAuthState() y re-renderiza TODO su árbol cuando los
  // permisos terminan de cargar (usePermissions()), así que este componente
  // se actualiza solo apenas el permiso real llega.
  const visibilidad = calcularVisibilidadDashboard({
    puedeVerCostos: hasPermission("cobranzas.see_cost"),
    esAdmin: isAdmin(),
  });

  // useCallback con `visibilidad.verCajaProyectada` como dependencia: si el
  // permiso todavía no cargó en el primer render (carga async de
  // usePermissions en App.jsx) y después llega, esta función cambia de
  // identidad y el efecto de abajo la vuelve a llamar — así el primer
  // refresco YA trae la caja proyectada, sin esperar los 5 minutos del
  // auto-refresh para que aparezca.
  const cargarDashboard = useCallback(async () => {
    try {
      const [dashboardData, cashflowData] = await Promise.all([
        api.get("/reports/dashboard"),
        visibilidad.verCajaProyectada ? api.get("/reports/cashflow?days=90") : Promise.resolve(null),
      ]);
      setDashboard(dashboardData);
      if (cashflowData) setCashflow(cashflowData);
    } catch (error) {
      // Gate data-exposure (review 19/08): sin el mensaje del error en la consola —
      // el usuario ya ve el fallback en criollo; el detalle tecnico no se loguea.
      console.error("Error cargando el dashboard");
    } finally {
      setLoading(false);
    }
  }, [visibilidad.verCajaProyectada]);

  // Auto-refresh cada 5 minutos (mismo intervalo que ya tenía el dashboard viejo).
  useEffect(() => {
    cargarDashboard();
    const interval = setInterval(cargarDashboard, 300000);
    return () => clearInterval(interval);
  }, [cargarDashboard]);

  if (loading) {
    return <DashboardSkeleton />;
  }

  if (!dashboard) {
    return (
      <div className="py-12 text-center">
        <p className="text-sm text-muted-foreground">No se pudieron cargar las métricas.</p>
      </div>
    );
  }

  return (
    <div className="animate-in fade-in space-y-6 duration-500">
      <DashboardHeader dolarRate={dashboard.bnaUsdSellerRate} onRefrescarDolar={cargarDashboard} />

      <div className="grid grid-cols-1 items-start gap-6 lg:grid-cols-2">
        {/* Columna TRABAJO */}
        <div className="space-y-6">
          <UpcomingTripsCard proximosViajes={dashboard.proximosViajes} />
          <PendingCollectionsCard reservasPendientes={dashboard.reservasPendientes} />
          <OpenBudgetsAndCrmCard
            presupuestosAbiertos={dashboard.presupuestos}
            posiblesClientes={dashboard.activePotentialCustomers}
          />
        </div>

        {/* Columna PLATA */}
        <div className="space-y-6">
          <MoneyKpiGrid
            porMoneda={dashboard.porMoneda}
            ventasDelMes={dashboard.ventasDelMes}
            cobrosDelMes={dashboard.cobrosDelMes}
            saldoPendiente={dashboard.saldoPendiente}
            margenBruto={dashboard.margenBruto}
            verMargenBruto={visibilidad.verMargenBruto}
            columnas={visibilidad.columnasGridKpi}
          />
          {visibilidad.verCajaProyectada ? <CashflowRhythmCard cashflow={cashflow} /> : null}
          {visibilidad.verInformes ? <ReportsLinkCard /> : null}
        </div>
      </div>
    </div>
  );
}
