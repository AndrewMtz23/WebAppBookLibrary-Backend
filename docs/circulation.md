# Circulación física — fase 10

## Contrato

`GET /api/circulation/policy` informa el modo compartido y la política. El valor inicial es `legacy`; la primera instancia lo persiste en `CirculationState/policy`. Después, cambiar `Circulation:Mode` no sustituye el modo persistido.

- `active`: reserva física de recogida por 48 horas continuas; entrega por personal inicia 14 días; una renovación de 7 días desde el vencimiento vigente, con aprobación y sin espera elegible.
- `draining`: bloquea altas y aprobaciones; conserva lectura, cancelación, recogida vigente, expiración y devolución. No promueve esperas. Los reintentos con clave de una creación confirmada recuperan el resultado anterior.
- `legacy`: mantiene el préstamo inmediato anterior y acceso digital. No volver a este modo si existen esperas, retenciones o préstamos `circulation-v1`.

Cada recogida conserva su snapshot. Las fechas existentes no se recalculan. El préstamo digital no consume inventario físico.

## API

Todas las operaciones privadas comprueban identidad activa. Las altas de lector requieren rol `user`; personal significa `librarian` o `admin`.

| Operación | Ruta |
|---|---|
| Reserva física | `POST /api/pickup-reservations` |
| Espera propia | `POST /api/waitlist`, `GET /api/waitlist/my`, `DELETE /api/waitlist/{id}` |
| Recogidas | `GET /api/pickup-reservations/my`; personal `GET /api/pickup-reservations` |
| Entrega/cancelación | `PUT /api/pickup-reservations/{id}/collect`, `/{id}/cancel` |
| Renovación | `POST /api/loans/{id}/renewal-requests` |
| Solicitudes | `GET /api/renewal-requests/my`; personal `GET /api/renewal-requests` |
| Decisión | `PUT /api/renewal-requests/{id}/decision` |
| Estado propio por libro | `GET /api/circulation/my/books/{id}` |
| Historial propio/personal | `GET /api/circulation/history/{id}` |
| Contadores actuales de personal | `GET /api/circulation/summary` |

Altas llevan `idempotencyKey` de 1–100 caracteres, ligado al actor, operación y contenido. Reutilizarlo con otro contenido produce 409. Conservar la clave al reintentar una respuesta perdida. Mutaciones de recogida/decisión requieren `expectedVersion > 0`. Motivos: máximo 500 caracteres; rechazo requiere motivo. Listas: `page`, `pageSize` entre 1 y 100; personal admite `status` y `search` de hasta 200 caracteres. Orden por fecha e ID. Espera propia no devuelve identidades ajenas.

`POST /api/loans` rechaza físicos en modo activo con `409 physical_pickup_required`. Para préstamos `circulation-v1`, devolver requiere personal; cancelar después de entregar está prohibido. No se borran préstamos activos ni préstamos de la nueva política. Las devoluciones/cancelaciones legacy en modo activo usan la misma asignación FIFO.

## Conciliación, activación y reversión

Primero respaldar y ensayar en una copia aislada. La herramienta no carga `.env`; exige conexión y base explícitas. Nunca ejecutarla por inferencia sobre Atlas.

```powershell
dotnet run --project WebAppBookLibrary.Migration -c Release -- --circulation --database BASE_COPIA --connection-env VARIABLE_URI --mode active
# Tras revisar el informe, respaldar y pausar escrituras:
dotnet run --project WebAppBookLibrary.Migration -c Release -- --circulation --database BASE_COPIA --connection-env VARIABLE_URI --mode active --apply --snapshot-confirmed --writes-paused
```

Dry-run no modifica datos. Apply valida, marca préstamos anteriores como `legacy` y cambia modo en una transacción. Conserva fechas e inventario. Bloquea diferencias entre disponibles, retenciones reales, préstamos y total; no inventa copias para compensarlas. Desplegar backend compatible y frontend juntos antes de activar. Todas las instancias deben usar las mismas duraciones.

Para detener altas, repetir el comando con `--mode draining`. Mantener el backend compatible mientras se entregan/devuelven/cancelan las operaciones existentes. No desplegar el binario anterior ignorando retenciones. La restauración se ensaya copiando BSON a otra base y reconstruyendo índices; verificar conciliación antes de usar el respaldo.

## Worker y observabilidad

Poll por defecto 30 s; lease durable de 2 min; plazo de lote 45 s. Expiración por fecha/ID y scans de referencias con cursores persistentes; lotes de 50, promoción con cursor de 100 entradas y descarte de hasta 20 cuentas inactivas por transacción. Una cola aún pendiente impide que una reserva directa la adelante. El siguiente ciclo continúa el trabajo.

`CirculationWorkerState` conserva lease, cursores y `LastCompletedAt`. Métricas del Meter `BookLibrary.Circulation`: `circulation.pickups.expired`, `circulation.conflicts`, `circulation.worker.failures`, `circulation.worker.duration` y `circulation.queue.oldest`. No tienen etiquetas de personas/libros. Conectar colector/alertas en el despliegue. Revisar errores, antigüedad de cola y conciliación antes de reintentar una operación manual.

Avisos e historial se escriben con la transición. El correo de oferta vuelve a comprobar vigencia, libro y cuenta; renovar invalida recordatorios del vencimiento anterior. El transporte real y las preferencias siguen el runbook de notificaciones.

## Verificación local

```powershell
$env:BOOK_LIBRARY_TEST_MONGO_URI='mongodb://127.0.0.1:27184/?replicaSet=booklibraryqa'
dotnet test -c Release
dotnet build WebAppBookLibrary.sln -c Release
```

Las pruebas crean y eliminan bases sintéticas. QaHost permite probar el modo activo con `Circulation__Mode=active` en una base nueva. Su ruta `__qa/circulation/expire/{id}` existe únicamente en el host aislado, nunca en la aplicación.
