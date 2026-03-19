import { useState, useEffect } from "react";
import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { 
  Terminal as TerminalIcon, 
  Smartphone, 
  Database, 
  Globe, 
  Clock,
  Activity
} from "lucide-react";
import LogTerminal from "./LogTerminal";

const hubUrl = (import.meta.env.VITE_API_URL || "http://localhost:5000").replace(/\/$/, "") + "/hubs/logs";

export default function LogsDashboard() {
  const [activeSubTab, setActiveSubTab] = useState("api");
  const [logs, setLogs] = useState([]);
  const [connection, setConnection] = useState(null);

  useEffect(() => {
    const newConnection = new HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Information)
      .build();

    setConnection(newConnection);

    // Fetch initial logs
    const fetchInitialLogs = async () => {
      try {
        const token = localStorage.getItem("token");
        const headers = { Authorization: `Bearer ${token}` };
        
        const [apiRes, botRes] = await Promise.all([
          fetch(`${(import.meta.env.VITE_API_URL || "http://localhost:5000").replace(/\/$/, "")}/api/logs/api-tail`, { headers }),
          fetch(`${(import.meta.env.VITE_API_URL || "http://localhost:5000").replace(/\/$/, "")}/api/logs/bot-tail`, { headers })
        ]);

        const apiLogs = await apiRes.json();
        const botLogs = await botRes.json();
        
        setLogs(prev => [...apiLogs, ...botLogs, ...prev].slice(-500));
      } catch (err) {
        console.error("Failed to fetch initial logs", err);
      }
    };

    fetchInitialLogs();
  }, []);

  useEffect(() => {
    if (connection) {
      connection.start()
        .then(() => {
          console.log("Connected to Logs Hub");
          connection.on("ReceiveLog", (message) => {
            setLogs(prev => {
              const newLogs = [...prev, message];
              return newLogs.slice(-500); // Keep last 500 lines
            });
          });
        })
        .catch(e => console.error("Connection failed: ", e));
    }
  }, [connection]);

  const filteredLogs = (category) => {
    const prefix = `[${category.toUpperCase()}]`;
    return logs.filter(log => log.startsWith(prefix));
  };

  const subTabs = [
    { id: "api", label: "API Backend", icon: Activity },
    { id: "whatsapp", label: "WhatsApp Bot", icon: Smartphone },
    { id: "db", label: "Base de Datos", icon: Database },
    { id: "web", label: "Servidor Web", icon: Globe },
    { id: "programming", label: "Programación", icon: Clock },
  ];

  return (
    <div className="space-y-4">
      {/* Sub-tabs Navigation */}
      <div className="flex flex-wrap gap-2 p-1 bg-slate-100 dark:bg-slate-800/50 rounded-xl w-fit">
        {subTabs.map(tab => {
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

      {/* Content Area */}
      <div className="animate-in fade-in duration-300">
        {activeSubTab === "programming" ? (
          <div className="h-[600px] bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden relative">
            <iframe
              src={`${import.meta.env.VITE_API_URL || "http://localhost:5000"}/api/auth/hangfire-login?token=${localStorage.getItem("token")}`}
              className="w-full h-full border-none"
              title="Hangfire Dashboard"
            />
          </div>
        ) : (
          <LogTerminal 
            category={activeSubTab} 
            logs={filteredLogs(activeSubTab)} 
          />
        )}
      </div>

      {activeSubTab === "db" && logs.filter(l => l.startsWith("[DB]")).length === 0 && (
         <p className="text-[10px] text-slate-500 italic px-2">
            Nota: Los logs de la base de datos se capturan solo durante operaciones críticas de migración o errores fatales.
         </p>
      )}

      {activeSubTab === "web" && logs.filter(l => l.startsWith("[WEB]")).length === 0 && (
         <p className="text-[10px] text-slate-500 italic px-2">
            Tip: Los logs de la web corresponden a los eventos del servidor Nginx y el bundle de Vite en producción.
         </p>
      )}
    </div>
  );
}
