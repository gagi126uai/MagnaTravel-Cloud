import { useState, useEffect } from "react";
import { Moon, Sun, Menu, ChevronLeft, ChevronRight } from "lucide-react";
import { useTheme } from "../context/ThemeContext";
import Sidebar from "./Sidebar";
import { Button } from "./ui/button";

export default function Layout({ children, onLogout, isAdmin }) {
    const { theme, setTheme } = useTheme();
    const [sidebarOpen, setSidebarOpen] = useState(false); // Mobile: overlay
    const [sidebarCollapsed, setSidebarCollapsed] = useState(false); // Desktop: collapsed icons only

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

    const toggleTheme = () => {
        setTheme(theme === "dark" ? "light" : "dark");
    };

    return (
        <div className="flex min-h-screen bg-background text-foreground transition-colors duration-300">
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
                        {/* Mobile menu button */}
                        <Button
                            variant="ghost"
                            size="icon"
                            onClick={() => setSidebarOpen(true)}
                            className="md:hidden h-9 w-9"
                        >
                            <Menu className="h-5 w-5" />
                            <span className="sr-only">Abrir menú</span>
                        </Button>

                        {/* Desktop collapse button */}
                        <Button
                            variant="ghost"
                            size="icon"
                            onClick={() => setSidebarCollapsed(!sidebarCollapsed)}
                            className="hidden md:flex h-9 w-9"
                            title={sidebarCollapsed ? "Expandir menú" : "Contraer menú"}
                        >
                            {sidebarCollapsed ? <ChevronRight className="h-5 w-5" /> : <ChevronLeft className="h-5 w-5" />}
                        </Button>

                        {/* Logo for mobile */}
                        <div className="md:hidden flex items-center gap-2">
                            <div className="flex h-8 w-8 items-center justify-center rounded-lg bg-indigo-600 text-white">
                                <span className="text-sm font-bold">MT</span>
                            </div>
                            <span className="font-semibold text-sm">MagnaTravel</span>
                        </div>

                        <h1 className="text-lg font-semibold hidden md:block">MagnaTravel ERP</h1>
                    </div>
                    <div className="flex items-center gap-2">
                        <Button
                            variant="ghost"
                            size="icon"
                            onClick={toggleTheme}
                            className="rounded-full h-9 w-9"
                        >
                            <Sun className="h-4 w-4 md:h-5 md:w-5 rotate-0 scale-100 transition-all dark:-rotate-90 dark:scale-0" />
                            <Moon className="absolute h-4 w-4 md:h-5 md:w-5 rotate-90 scale-0 transition-all dark:rotate-0 dark:scale-100" />
                            <span className="sr-only">Cambiar tema</span>
                        </Button>
                        <div className="h-8 w-8 rounded-full bg-primary/20 flex items-center justify-center text-xs md:text-sm font-medium text-primary border border-primary/20">
                            AD
                        </div>
                    </div>
                </header>

                {/* Main Content Area */}
                <main className="flex-1 p-3 md:p-6 lg:p-8 overflow-x-hidden">
                    <div className="mx-auto max-w-7xl animate-in fade-in slide-in-from-bottom-4 duration-500">
                        {children}
                    </div>
                </main>
            </div>
        </div>
    );
}
