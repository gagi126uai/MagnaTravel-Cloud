# 2026-06-28 — ADR-041: Proveedores / Cuentas por Pagar (tanda completa)

## Qué se hizo (en simple)

Se rehízo y completó el módulo de **Proveedores / Cuentas por Pagar**. Todo quedó construido, revisado y con los tests en verde, **sin commitear** (lo sube Gastón; subir a `main` despliega solo a producción).

### 1. Cuenta del proveedor como extracto
La pantalla del proveedor pasó a ser un **extracto tipo banco con saldo corriente por moneda** (igual que la cuenta del cliente). De paso se arregló un bug donde el resumen **sumaba pesos con dólares** en un solo número. El pago al proveedor pasó de ventana flotante a **en línea**, y se puede **editar/corregir** un pago. El saldo a favor con un operador se muestra con **cartel verde**.

### 2. Cuentas bancarias (agencia / clientes / proveedores)
Función nueva: guardar cuentas bancarias de los tres. Datos: banco, tipo, CBU, alias, titular, CUIT, moneda. Obligatorio: **alias o CBU + titular + moneda**. El CBU/alias se ven **tapados** en las listas y completos en el detalle (y queda registrado quién lo miró). Se puede marcar una cuenta **principal por moneda**. Las cuentas de la agencia se administran en **Configuración → Agencia**; las de proveedor/cliente en una sección de su ficha. Al **pagarle a un proveedor** aparece su CBU con botón **Copiar**.

### 3. Saldo a favor "de verdad" (operador y cliente)
Antes el saldo a favor solo se veía. Ahora se puede **usar en otra reserva** del mismo dueño y misma moneda, y **revertir** la aplicación desde el extracto (motivo opcional). El sistema topea por lo que esa reserva debe y por el saldo disponible, y nunca cruza pesos con dólares. Se construyó para el **operador y el cliente**.

### 4. Vencimiento opcional de la deuda con el operador
Se puede cargar un **plazo de pago por defecto** del proveedor; de ahí sale una **fecha de vencimiento sugerida** por servicio. No bloquea nada (seguimos prepago); sirve para priorizar y avisar.

### 5. Esperando reembolso + reembolso tardío
Cuando se anula una reserva ya pagada al operador, queda plata que el operador tiene que devolver. Ahora hay una **bandeja "Reembolsos a cobrar"** (todos juntos) y la lista también en la ficha del proveedor, con **semáforo** (a tiempo / por vencer / vencido / abandonado); los vencidos quedan en **rojo** y no desaparecen. El monto se muestra siempre como **estimado** (el operador deduce). Si el operador devuelve **tarde** (ya dado por perdido), hay un botón para **reabrir** y registrar el ingreso por Caja.

### 6. Datos bancarios de la agencia de cara al cliente
Los datos bancarios de la agencia aparecen en el **PDF del recibo de cobro** y en un **recuadro en pantalla al cobrar** (cuando el método es transferencia). Y al **devolverle plata a un cliente** por transferencia, se muestra el **CBU del cliente** con botón Copiar.

## Decisiones de Gastón (cerradas)
- Cuentas bancarias de los tres dueños; obligatorio alias|CBU + titular + moneda; principal sí; agencia en Configuración; mostrar al cliente en PDF + pantalla; CBU del cliente para devoluciones sí.
- Vencimiento del operador: opcional.
- Saldo a favor: usarlo en otra reserva (operador y cliente); revertir desde el extracto; motivo al revertir opcional.
- Reembolso tardío: armar el camino (va al saldo del cliente, caso normal).
- **PDF de presupuesto: para después** (hoy no existe un documento de presupuesto en el sistema).

## Cómo está modelado (técnico, resumido)
- **Migraciones nuevas**: `Adr041_M1` (BankAccounts), `Adr041_M2` (ledger SupplierCreditEntry/Application + backfill de saldos negativos), `Adr041_M3` (Supplier.DefaultPaymentTermDays), `Adr041_M4` (BankAccount.IsPrimary). Todas aditivas; falta validarlas contra Postgres real en el deploy.
- **Saldo a favor del operador**: dejó de ser un número derivado (`SupplierBalanceByCurrency.Balance` negativo) y pasó a ser un **crédito consumible de primera clase** (`SupplierCreditEntry`), espejo del modelo del cliente (`ClientCreditEntry`/`Withdrawal`). El `apply` re-deriva el sobrepago real y topea por la deuda viva del destino (cierra el "crédito fantasma"). El lado cliente reusa el puente `Adr030_M1`.
- **Cuentas bancarias**: una entidad polimórfica `BankAccount` (`OwnerType` Agency/Customer/Supplier, FK lógica). Enums sin `JsonStringEnumConverter` → viajan como **int** en el body. Escrituras devuelven el shape **enmascarado** (no exponen el CBU en claro sin auditar).
- **Esperando reembolso**: read-model sobre `BookingCancellation` + `OperatorRefundService`. El tardío reabre `AbandonedByOperator → AwaitingOperatorRefund` (la reserva sigue cancelada).
- Tests: backend **2865/2865**, frontend **1335/1335**, ambos build OK.

## Pendientes / follow-ups (diferidos, no bloqueantes)
- PDF de presupuesto (no existe; diferido por Gastón).
- Índice único parcial para "una principal por dueño+moneda" (hoy lo garantiza el servicio; candado BD requiere `DEFERRABLE`).
- Reconciler del saldo a favor del operador también en cambios de servicio (hoy solo en pagos; el `apply` ya es seguro, es solo que la pantalla puede mostrarse optimista).
- Reembolso tardío que caiga como saldo del **operador** en vez del cliente (necesita regla de negocio de cuándo "ya no corresponde al cliente").

## Qué tiene que hacer Gastón
1. Decidir el commit (todo el working tree, 1 commit).
2. Subir a `main` (eso despliega solo a producción).
3. En el deploy se validan las 4 migraciones contra Postgres.
