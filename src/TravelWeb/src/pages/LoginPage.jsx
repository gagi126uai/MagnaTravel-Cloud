import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { api } from "../api";
import { setCurrentUser } from "../auth";
import { showError, showSuccess } from "../alerts";
import { Mail, Lock, ArrowRight, Loader2, CheckCircle2 } from "lucide-react";
import clsx from "clsx";

export default function LoginPage() {
  const navigate = useNavigate();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [isLoading, setIsLoading] = useState(false);
  const [keepSignedIn, setKeepSignedIn] = useState(false);

  const handleSubmit = async (event) => {
    event.preventDefault();
    if (isLoading) return;

    setIsLoading(true);
    try {
      const response = await api.post("/auth/login", {
        email,
        password,
        rememberMe: keepSignedIn,
      });

      setCurrentUser(response.user);
      showSuccess("Bienvenido de nuevo.");
      navigate("/dashboard");
    } catch (err) {
      showError(err.message || "Credenciales incorrectas o error de conexion.");
    } finally {
      setIsLoading(false);
    }
  };

  return (
    <div className="flex min-h-screen w-full bg-slate-50 dark:bg-slate-950">
      <div className="hidden lg:flex lg:w-1/2 relative bg-slate-900 overflow-hidden">
        <div className="absolute inset-0 bg-[url('https://images.unsplash.com/photo-1469854523086-cc02fe5d8800?q=80&w=2021&auto=format&fit=crop')] bg-cover bg-center opacity-40 mix-blend-overlay"></div>
        <div className="absolute inset-0 bg-gradient-to-t from-slate-900/90 via-slate-900/40 to-slate-900/10"></div>

        <div className="relative z-10 flex flex-col justify-between p-12 text-white w-full">
          <div className="flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-indigo-500/20 text-indigo-400 backdrop-blur-sm border border-indigo-500/30">
              <span className="font-bold text-lg">MT</span>
            </div>
            <span className="text-xl font-medium tracking-wide">MagnaTravel</span>
          </div>

          <div className="space-y-6 max-w-lg">
            <h1 className="text-4xl font-bold leading-tight">
              Gestiona tus viajes <br />
              <span className="text-indigo-400">con excelencia.</span>
            </h1>
            <p className="text-lg text-slate-300 leading-relaxed">
              La plataforma integral para agencias de viaje modernas. Control total sobre expedientes, proveedores y finanzas en un solo lugar.
            </p>

            <div className="flex gap-4 pt-4">
              <div className="flex items-center gap-2 text-sm text-slate-400">
                <CheckCircle2 className="h-4 w-4 text-indigo-400" />
                <span>Gestion de Expedientes</span>
              </div>
              <div className="flex items-center gap-2 text-sm text-slate-400">
                <CheckCircle2 className="h-4 w-4 text-indigo-400" />
                <span>Control Financiero</span>
              </div>
            </div>
          </div>

          <div className="text-sm text-slate-500">
            &copy; {new Date().getFullYear()} MagnaTravel Cloud. Todos los derechos reservados.
          </div>
        </div>
      </div>

      <div className="flex w-full items-center justify-center p-8 lg:w-1/2 bg-white dark:bg-slate-950">
        <div className="w-full max-w-sm space-y-8 animate-in fade-in slide-in-from-bottom-4 duration-700">
          <div className="text-center lg:text-left">
            <div className="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-xl bg-indigo-600/10 text-indigo-600 lg:hidden dark:bg-indigo-500/10 dark:text-indigo-400">
              <span className="font-bold text-xl">MT</span>
            </div>
            <h2 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">
              Iniciar sesion
            </h2>
            <p className="mt-2 text-sm text-slate-500 dark:text-slate-400">
              Bienvenido de nuevo. Por favor ingresa tus datos.
            </p>
          </div>

          <form onSubmit={handleSubmit} className="space-y-5">
            <div className="space-y-2">
              <label className="text-sm font-medium text-slate-700 dark:text-slate-300">
                Email corporativo
              </label>
              <div className="relative">
                <Mail className="absolute left-3 top-1/2 -translate-y-1/2 h-5 w-5 text-slate-400" />
                <input
                  type="email"
                  required
                  className="w-full rounded-lg border border-slate-200 bg-white px-10 py-2.5 text-sm text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500 dark:border-slate-800 dark:bg-slate-900 dark:text-white dark:focus:border-indigo-500"
                  placeholder="nombre@empresa.com"
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                />
              </div>
            </div>

            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <label className="text-sm font-medium text-slate-700 dark:text-slate-300">
                  Contrasena
                </label>
                <a href="#" className="text-xs font-medium text-indigo-600 hover:text-indigo-500 dark:text-indigo-400">
                  Olvidaste tu contrasena?
                </a>
              </div>
              <div className="relative">
                <Lock className="absolute left-3 top-1/2 -translate-y-1/2 h-5 w-5 text-slate-400" />
                <input
                  type="password"
                  required
                  className="w-full rounded-lg border border-slate-200 bg-white px-10 py-2.5 text-sm text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500 dark:border-slate-800 dark:bg-slate-900 dark:text-white dark:focus:border-indigo-500"
                  placeholder="********"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                />
              </div>
            </div>

            <div className="flex items-center space-x-2">
              <input
                id="keep-signed-in"
                type="checkbox"
                checked={keepSignedIn}
                onChange={(e) => setKeepSignedIn(e.target.checked)}
                className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-900"
              />
              <label
                htmlFor="keep-signed-in"
                className="text-sm font-medium text-slate-600 dark:text-slate-400"
              >
                Mantener sesion iniciada
              </label>
            </div>

            <button
              type="submit"
              disabled={isLoading}
              className={clsx(
                "group flex w-full items-center justify-center gap-2 rounded-lg py-2.5 text-sm font-semibold text-white shadow-md transition-all hover:bg-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:opacity-50 disabled:cursor-not-allowed",
                "bg-indigo-600"
              )}
            >
              {isLoading ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : (
                <>
                  Ingresar al sistema
                  <ArrowRight className="h-4 w-4 transition-transform group-hover:translate-x-1" />
                </>
              )}
            </button>
          </form>

          <div className="relative">
            <div className="absolute inset-0 flex items-center">
              <span className="w-full border-t border-slate-200 dark:border-slate-800" />
            </div>
            <div className="relative flex justify-center text-xs uppercase">
              <span className="bg-white px-2 text-slate-500 dark:bg-slate-950 dark:text-slate-400">
                Acceso administrado
              </span>
            </div>
          </div>

          <div className="text-center text-sm text-slate-500 dark:text-slate-400">
            El alta de usuarios quedo restringida a administradores.
          </div>
        </div>
      </div>
    </div>
  );
}
