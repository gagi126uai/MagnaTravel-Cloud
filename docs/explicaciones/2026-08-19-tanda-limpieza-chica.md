# Tanda de limpieza chica (19/08/2026) — explicado en fácil

Después de la obra grande del dashboard y las cuentas corrientes, quedó una
lista de cositas anotadas "para la próxima pasada". Esta sesión las saldó
todas de una (PR-7: deuda cero). Commit: `fc66d77f`.

## Qué se ve distinto

1. **Menú: aparece "Reportes" en GESTIÓN.** La pantalla global de reportes
   existía hace rato (`/reports`) pero solo se llegaba tipeando la dirección.
   Ahora tiene su entrada en el menú, entre Comisiones y Administración, y la
   ve quien tenga el permiso de reportes. Ojo: **Informes** (`/analytics`)
   sigue SIN entrada propia a propósito — la spec del dashboard (18/08, §5.5)
   decidió que su única puerta es la tarjeta "Informes completos" del Inicio.

2. **Informes: la plata ya no mezcla pesos con dólares.** La pantalla de
   informes mostraba "Saldo actual" y las proyecciones a 30/60/90 días como UN
   solo número que sumaba ARS + USD (violaba la regla P-3: las monedas nunca
   se suman). Ahora cada tarjeta muestra una línea por moneda con su bandera,
   y el gráfico de flujo de caja se dibuja un gráfico por moneda — igual que
   la tarjeta "Ritmo de cobros y pagos" del Inicio (reusa exactamente las
   mismas piezas). Quien no puede ver costos no ve la curva de pagos a
   operadores (ni un $0 falso: directamente no está).

3. **Facturación: filtro nuevo "Error anulación".** El chip rojo existía en
   la tabla pero no se podía filtrar por él. El backend ya lo soportaba;
   faltaba la opción en el selector.

4. **El esqueleto de carga del Inicio ahora tiene la forma del dashboard
   nuevo** (cabecera + dos columnas), no la del viejo.

5. **El botón "Nuevo presupuesto" del Inicio usa el ícono "+"** como en
   Reservas (es un alta, no un documento).

## Qué NO se ve pero cambió

- **El motivo de anulación de una factura de operador ya no viaja a quien no
  puede ver costos** (regla F-14). El motivo suele mencionar montos ("se
  anuló por diferencia de USD 200"); ahora el servidor lo manda solo con el
  permiso de costos. En la base no se pierde nada: sigue guardado (rastro
  intacto), solo se oculta en la respuesta.
- **El endpoint de flujo de caja ahora manda los saldos por moneda** además
  de los escalares viejos (que quedan por compatibilidad pero ya nadie
  debería leer). El enmascarado de costos se hereda de la serie diaria que ya
  estaba enmascarada — no se abre ningún canal para deducir costos restando.
- **Código muerto borrado**: los restos de la vieja solapa de facturación
  (`InvoicingTab`/`InvoiceSection` — solo vivía `WorkItemSection`, que ahora
  tiene su propio archivo), dos chips muertos en la ficha de reserva, un
  helper huérfano y cuatro comentarios que apuntaban a archivos borrados.
- **Los tests de las libs del dashboard ahora corren en CI**: el glob de
  `npm test` no incluía `features/dashboard/lib/*.test.mjs` (gap preexistente
  que esta tanda expuso al reusar esa lib en Informes).

## Reviews

Backend, frontend, seguridad/datos y el gate de exposición de internals:
los cuatro aprobaron sin bloqueantes. 3806 tests de frontend + unit tests de
backend verdes en local; suite completa en CI.

## Deuda anotada (no salió en esta tanda)

- En Informes, las solapas de vendedores / destinos / año contra año siguen
  usando totales que mezclan monedas (mismo problema P-3 que se arregló en la
  de caja). Encaja natural en la obra multimoneda (ADR-011).
- Los endpoints de vendedores/destinos/YoY exigen rol Admin duro en el
  backend: un usuario con permiso de reportes que no sea admin vería fallar
  esas solapas. Preexistente, anotado.
- Los tests `.mjs` "réplica" no importan el módulo real (patrón conocido del
  proyecto): riesgo estructural de que un cambio real deje la réplica verde.
