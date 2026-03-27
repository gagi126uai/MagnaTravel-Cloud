import { useState, useEffect } from "react";
import { Moon, Sun, Menu, Search, AlertCircle } from "lucide-react";
import { useTheme } from "../context/ThemeContext";
import Sidebar from "./Sidebar";
import { Button } from "./ui/button";
import ChangePasswordModal from "./ChangePasswordModal";
import SearchPalette from "./SearchPalette";
import NotificationBell from "./NotificationBell";
import { api } from "../api";
import UrgentBannerStack from "./UrgentBannerStack";

export default function Layout({ children, onLogout, isAdmin }) {
    const { theme, setTheme } = useTheme();
    const [sidebarOpen, setSidebarOpen] = useState(false); // Mobile: overlay
    const [sidebarCollapsed, setSidebarCollapsed] = useState(false); // Desktop: collapsed icons only
    const [afipSettings, setAfipSettings] = useState(null);

    // Close mobile sidebar on route change
    useEffect(() => {
        setSidebarOpen(false);
    }, [children]);

    // Close mobile sidebar on window resize to desktop
    useEffect(() => {
        const handleResize = () => {
            if (window.innerWidth >= 768) {
                setSidebarOpen(false);
            }
        };
        window.addEventListener("resize", handleResize);
        return () => window.removeEventListener("resize", handleResize);
    }, []);

    useEffect(() => {
        const fetchAfipStatus = async () => {
            try {
                const data = await api.get("/afip/settings");
                setAfipSettings(data);
            } catch (error) {
                console.warn("Could not fetch AFIP settings for banner");
            }
        };
        fetchAfipStatus();
    }, []);

    const toggleTheme = () => {
        setTheme(theme === "dark" ? "light" : "dark");
    };

    const [showPasswordModal, setShowPasswordModal] = useState(false);
    const [userMenuOpen, setUserMenuOpen] = useState(false);
    const [searchOpen, setSearchOpen] = useState(false);

    // Global Ctrl+K handler
    useEffect(() => {
        const handleKeyDown = (e) => {
            if ((e.ctrlKey || e.metaKey) && e.key === "k") {
                e.preventDefault();
                setSearchOpen(prev => !prev);
            }
        };
        window.addEventListener("keydown", handleKeyDown);
        return () => window.removeEventListener("keydown", handleKeyDown);
    }, []);

    return (
        <div className="flex flex-col min-h-screen bg-background text-foreground transition-colors duration-300">
            {afipSettings && !afipSettings.isProduction && (
                <div className="bg-gradient-to-r from-amber-500 via-orange-500 to-amber-500 text-white text-[9px] md:text-[10px] py-1.5 px-4 text-center font-black uppercase tracking-[0.2em] shadow-lg z-[60] flex items-center justify-center gap-3 border-b border-amber-400/20">
                    <AlertCircle className="h-3.5 w-3.5 animate-pulse" />
                    <span className="drop-shadow-sm">Modo Homologación Activo • Comprobantes sin validez legal</span>
                    <AlertCircle className="h-3.5 w-3.5 animate-pulse" />
                </div>
            )}

            <UrgentBannerStack />

            <div className="flex flex-1 relative overflow-hidden">
                {/* Mobile Overlay */}
                {sidebarOpen && (
                    <div
                        className="fixed inset-0 z-40 bg-black/60 backdrop-blur-sm md:hidden"
                        onClick={() => setSidebarOpen(false)}
                    />
                )}

                {/* Sidebar - Mobile: slide-in overlay, Desktop: fixed */}
                <Sidebar
                    onLogout={onLogout}
                    isAdmin={isAdmin}
                    collapsed={sidebarCollapsed}
                    onToggleCollapse={() => setSidebarCollapsed(!sidebarCollapsed)}
                    className={`
                        fixed h-full z-50 transition-all duration-300 ease-in-out
                        ${sidebarCollapsed ? 'w-20' : 'w-64'}
                        ${sidebarOpen ? 'translate-x-0' : '-translate-x-full'}
                        md:translate-x-0
                    `}
                    onCloseMobile={() => setSidebarOpen(false)}
                />

                {/* Main Content */}
                <div className={`
                    flex-1 flex flex-col transition-all duration-300
                    ${sidebarCollapsed ? 'md:ml-20' : 'md:ml-64'}
                `}>
                    {/* Header */}
                    <header className="sticky top-0 z-20 flex h-14 md:h-16 items-center justify-between border-b bg-background/95 px-3 md:px-6 backdrop-blur supports-[backdrop-filter]:bg-background/60">
                        <div className="flex items-center gap-2 md:gap-3">
                            <Button
                                variant="ghost"
                                size="icon"
                                onClick={() => {
                                    if (window.innerWidth < 768) {
                                        setSidebarOpen(true);
                                    } else {
                                        setSidebarCollapsed(!sidebarCollapsed);
                                    }
                                }}
                                className="h-9 w-9"
                                title="Menú"
                            >
                                <Menu className="h-5 w-5" />
                            </Button>

                            <div className="md:hidden flex items-center gap-2">
                                <div className="flex h-8 w-8 items-center justify-center rounded-lg bg-indigo-600 text-white">
                                    <span className="text-sm font-bold">MT</span>
                                </div>
                                <span className="font-semibold text-sm">MagnaTravel</span>
                            </div>

                            <h1 className="text-lg font-semibold hidden md:block">MagnaTravel ERP</h1>
                        </div>
                        <div className="flex items-center gap-2">
                            <button
                                onClick={() => setSearchOpen(true)}
                                className="hidden sm:flex items-center gap-2 px-3 py-1.5 text-sm text-slate-500 bg-slate-100 dark:bg-slate-800 dark:text-slate-400 rounded-lg hover:bg-slate-200 dark:hover:bg-slate-700 transition-colors border border-slate-200 dark:border-slate-700"
                            >
                                <Search className="h-3.5 w-3.5" />
                                <span className="text-xs">Buscar...</span>
                                <kbd className="ml-2 text-[10px] font-mono px-1.5 py-0.5 bg-white dark:bg-slate-900 rounded border border-slate-200 dark:border-slate-600">Ctrl+K</kbd>
                            </button>
                            <Button
                                variant="ghost"
                                size="icon"
                                onClick={() => setSearchOpen(true)}
                                className="sm:hidden h-9 w-9"
                            >
                                <Search className="h-4 w-4" />
                            </Button>
                            <Button
                                variant="ghost"
                                size="icon"
                                onClick={toggleTheme}
                                className="rounded-full h-9 w-9"
                            >
                                <Sun className="h-4 w-4 md:h-5 md:w-5 rotate-0 scale-100 transition-all dark:-rotate-90 dark:scale-0" />
                                <Moon className="absolute h-4 w-4 md:h-5 md:w-5 rotate-90 scale-0 transition-all dark:rotate-0 dark:scale-100" />
                            </Button>

                            <NotificationBell />

                            <div className="relative">
                                <button
                                    onClick={() => setUserMenuOpen(!userMenuOpen)}
                                    className="h-9 w-9 rounded-full bg-primary/20 flex items-center justify-center text-xs md:text-sm font-medium text-primary border border-primary/20 hover:bg-primary/30 transition-colors focus:outline-none focus:ring-2 focus:ring-ring focus:ring-offset-2"
                                >
                                    AD
                                </button>

                                {userMenuOpen && (
                                    <>
                                        <div className="fixed inset-0 z-10" onClick={() => setUserMenuOpen(false)} />
                                        <div className="absolute right-0 mt-2 w-48 rounded-md shadow-lg bg-popover ring-1 ring-black ring-opacity-5 z-20 animate-in fade-in zoom-in-95 duration-150">
                                            <div className="py-1">
                                                <button
                                                    onClick={() => {
                                                        setUserMenuOpen(false);
                                                        setShowPasswordModal(true);
                                                    }}
                                                    className="block px-4 py-2 text-sm text-popover-foreground hover:bg-muted w-full text-left"
                                                >
                                                    Cambiar Contraseña
                                                </button>
                                                <div className="border-t border-border my-1"></div>
                                                <button
                                                    onClick={() => {
                                                        setUserMenuOpen(false);
                                                        onLogout();
                                                    }}
                                                    className="block px-4 py-2 text-sm text-red-600 hover:bg-red-50 dark:hover:bg-red-900/20 w-full text-left"
                                                >
                                                    Cerrar Sesión
                                                </button>
                                            </div>
                                        </div>
                                    </>
                                )}
                            </div>
                        </div>
                    </header>

                    <main className="flex-1 p-3 md:p-6 lg:p-8 overflow-y-auto">
                        <div className="mx-auto max-w-7xl animate-in fade-in slide-in-from-bottom-4 duration-500">
                            {children}
                        </div>
                    </main>
                </div>
            </div>

            <ChangePasswordModal
                isOpen={showPasswordModal}
                onClose={() => setShowPasswordModal(false)}
            />

            <SearchPalette
                isOpen={searchOpen}
                onClose={(toggle) => {
                    if (toggle === true) setSearchOpen(true);
                    else setSearchOpen(false);
                }}
            />
        </div>
    );
}
