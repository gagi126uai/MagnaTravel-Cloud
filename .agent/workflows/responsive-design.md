---
description: Guía de diseño responsivo para páginas del ERP
---

# Diseño Responsivo - MagnaTravel ERP

## Breakpoints

```
sm: 640px   - Smartphones grandes
md: 768px   - Tablets / Sidebar visible
lg: 1024px  - Laptops
xl: 1280px  - Desktop
```

## Reglas Generales

### 1. Contenedores
```jsx
// Main content wrapper
<div className="p-3 md:p-6 lg:p-8">
  <div className="mx-auto max-w-7xl">
    {content}
  </div>
</div>
```

### 2. Títulos de Página
```jsx
<div className="flex flex-col gap-4 md:flex-row md:items-center md:justify-between">
  <div>
    <h1 className="text-xl md:text-2xl font-bold">Título</h1>
    <p className="text-sm text-muted-foreground">Descripción opcional</p>
  </div>
  <div className="flex gap-2">
    {/* Botones de acción */}
  </div>
</div>
```

### 3. Grids Adaptativos
```jsx
// Cards estadísticas
<div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">

// Formularios 2 columnas
<div className="grid gap-4 sm:grid-cols-2">

// Contenido principal + sidebar
<div className="grid lg:grid-cols-3 gap-6">
  <div className="lg:col-span-2">{main}</div>
  <div>{sidebar}</div>
</div>
```

### 4. Tablas Responsivas
```jsx
<div className="rounded-xl border bg-card overflow-hidden">
  <div className="overflow-x-auto">
    <table className="w-full text-sm min-w-[600px]">
      {/* min-w para scroll horizontal en mobile */}
    </table>
  </div>
</div>
```

### 5. Modales
```jsx
<div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60 backdrop-blur-sm">
  <div className="w-full max-w-lg max-h-[90vh] overflow-y-auto rounded-xl border bg-card p-4 md:p-6">
    {content}
  </div>
</div>
```

### 6. Botones
```jsx
// Botón con texto + icono (desktop), solo icono (mobile)
<button className="inline-flex items-center gap-2 px-3 py-2 md:px-4">
  <Icon className="h-4 w-4" />
  <span className="hidden sm:inline">Texto</span>
</button>

// Botón icon-only
<button className="h-8 w-8 md:h-9 md:w-9">
  <Icon className="h-4 w-4" />
</button>
```

### 7. Texto Adaptativo
```jsx
<p className="text-sm md:text-base">Texto normal</p>
<h2 className="text-lg md:text-xl font-semibold">Subtítulo</h2>
<span className="text-xs md:text-sm text-muted-foreground">Auxiliar</span>
```

### 8. Espaciado
```jsx
// Gap adaptativo
<div className="space-y-4 md:space-y-6">
<div className="gap-2 md:gap-4">
<div className="p-3 md:p-4 lg:p-6">
```

### 9. Ocultar/Mostrar por Dispositivo
```jsx
<div className="hidden md:block">Solo desktop</div>
<div className="md:hidden">Solo mobile</div>
<div className="hidden sm:flex lg:hidden">Solo tablet</div>
```

### 10. Cards Resumen (Stats)
```jsx
<div className="rounded-xl border bg-card p-3 md:p-4 shadow-sm">
  <div className="flex items-center gap-2 text-muted-foreground mb-1">
    <Icon className="h-4 w-4" />
    <span className="text-xs md:text-sm font-medium">Título</span>
  </div>
  <p className="text-xl md:text-2xl font-bold">$1,234</p>
  <p className="text-xs text-muted-foreground mt-1">Descripción</p>
</div>
```

## Checklist Pre-Deploy

- [ ] Probar en viewport 375px (iPhone SE)
- [ ] Probar en viewport 768px (iPad)
- [ ] Probar en viewport 1280px (Desktop)
- [ ] Verificar scroll horizontal en tablas
- [ ] Verificar modales no cortados
- [ ] Verificar botones touch-friendly (min 44px)
