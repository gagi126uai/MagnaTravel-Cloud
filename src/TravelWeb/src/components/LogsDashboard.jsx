import { useEffect, useState } from "react";
import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { api, buildAppUrl } from "../api";
import {
  Smartphone,
  Database,
  Globe,
  Clock,
  Activity,
} from "lucide-react";
import LogTerminal from "./LogTerminal";

const hubUrl = buildAppUrl("/hubs/logs");

export default function LogsDashboard() {
  const [activeSubTab, setActiveSubTab] = useState("api");
  const [logs, setLogs] = useState([]);
  const [hangfireUrl, setHangfireUrl] = useState("");
  const [hangfireLoading, setHangfireLoading] = useState(false);
  const [hangfireError, setHangfireError] = useState("");

  useEffect(() => {
    const fetchInitialLogs = async () => {
      try {
        const [apiLogs, botLogs] = await Promise.all([
          api.get("/logs/api-tail"),
          api.get("/logs/bot-tail"),
        ]);

        setLogs([...(apiLogs || []), ...(botLogs || [])].slice(-500));
      } catch (error) {
        console.error("Failed to fetch initial logs", error);
      }
    };

    const connection = new HubConnectionBuilder()
      .withUrl(hubUrl, { withCredentials: true })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Information)
      .build();

    connection.on("ReceiveLog", (message) => {
      setLogs((prev) => [...prev, message].slice(-500));
    });

    fetchInitialLogs();

    connection.start().catch((error) => {
      console.error("Connection failed:", error);
    });

    return () => {
      connection.off("ReceiveLog");
      connection.stop().catch(() => undefined);
    };
  }, []);

  useEffect(() => {
    if (activeSubTab !== "programming" || hangfireUrl || hangfireLoading) {
      return;
    }

    let cancelled = false;

    const prepareHangfireSession = async () => {
      setHangfireLoading(true);
      setHangfireError("");

      try {
        await api.post("/auth/hangfire-session");
        if (!cancelled) {
          setHangfireUrl(buildAppUrl("/hangfire"));
        }
      } catch (error) {
        if (!cancelled) {
          setHangfireError(error.message || "No se pudo abrir el panel de tareas.");
        }
      } finally {
        if (!cancelled) {
          setHangfireLoading(false);
        }
      }
    };

    prepareHangfireSession();

    return () => {
      cancelled = true;
    };
  }, [activeSubTab, hangfireLoading, hangfireUrl]);

  const filteredLogs = (category) => {
    const prefix = `[${category.toUpperCase()}]`;
    return logs.filter((log) => log.startsWith(prefix));
  };

  const subTabs = [
    { id: "api", label: "API Backend", icon: Activity },
    { id: "whatsapp", label: "WhatsApp Bot", icon: Smartphone },
    { id: "db", label: "Base de Datos", icon: Database },
    { id: "web", label: "Servidor Web", icon: Globe },
    { id: "programming", label: "Programacion", icon: Clock },
  ];

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap gap-2 p-1 bg-slate-100 dark:bg-slate-800/50 rounded-xl w-fit">
        {subTabs.map((tab) => {
          const Icon = tab.icon;
          const isActive = activeSubTab === tab.id;
          return (
            <button
              key={tab.id}
              onClick={() => setActiveSubTab(tab.id)}
              className={`
                flex items-center gap-2 px-3 py-1.5 text-xs font-semibold rounded-lg transition-all
                ${isActive
                  ? "bg-white dark:bg-slate-700 text-indigo-600 dark:text-indigo-400 shadow-sm"
                  : "text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200"
                }
              `}
            >
              <Icon className="h-3.5 w-3.5" />
              {tab.label}
            </button>
          );
        })}
      </div>

      <div className="animate-in fade-in duration-300">
        {activeSubTab === "programming" ? (
          <div className="h-[600px] bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden relative">
            {hangfireLoading ? (
              <div className="flex h-full items-center justify-center text-sm text-slate-500 dark:text-slate-400">
                Preparando acceso seguro a Hangfire...
              </div>
            ) : hangfireError ? (
              <div className="flex h-full items-center justify-center px-6 text-center text-sm text-rose-600 dark:text-rose-400">
                {hangfireError}
              </div>
            ) : (
              <iframe
                src={hangfireUrl}
                className="w-full h-full border-none"
                title="Hangfire Dashboard"
              />
            )}
          </div>
        ) : (
          <LogTerminal category={activeSubTab} logs={filteredLogs(activeSubTab)} />
        )}
      </div>

      {activeSubTab === "db" && logs.filter((log) => log.startsWith("[DB]")).length === 0 && (
        <p className="text-[10px] text-slate-500 italic px-2">
          Nota: Los logs de la base de datos se capturan solo durante operaciones criticas de migracion o errores fatales.
        </p>
      )}

      {activeSubTab === "web" && logs.filter((log) => log.startsWith("[WEB]")).length === 0 && (
        <p className="text-[10px] text-slate-500 italic px-2">
          Tip: Los logs de la web corresponden a los eventos del servidor Nginx y el bundle de Vite en produccion.
        </p>
      )}
    </div>
  );
}
