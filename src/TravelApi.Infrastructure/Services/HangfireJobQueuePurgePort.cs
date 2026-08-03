using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Implementacion real de <see cref="IHangfireJobQueuePurgePort"/> contra el storage de Hangfire
/// (Postgres, configurado en <c>Program.cs</c>). Ver el XML doc de la interfaz para el "por que" completo.
///
/// <para><b>Truco de framework</b> (trainee/junior): Hangfire NO tiene un metodo "borrar todo lo
/// encolado" de una sola llamada. Hay que RECORRER cada cola (<see cref="IMonitoringApi.Queues"/>) y la
/// lista de programados (<see cref="IMonitoringApi.ScheduledJobs"/>) a mano, juntando los IDs, y borrar
/// cada uno con <see cref="BackgroundJobClientExtensions.Delete"/>. "Borrar" en Hangfire no es un DELETE
/// de fila: cambia el job al estado <c>Deleted</c> (queda su rastro en el storage por un tiempo, como
/// cualquier otro estado — no se pierde silenciosamente).</para>
///
/// <para><b>Por que recibe <see cref="JobStorage"/> por constructor en vez de usar <c>JobStorage.Current</c>
/// directamente</b> (decision tecnica, retomo 2026-08-03): <c>JobStorage.Current</c> es un singleton ESTATICO
/// de todo el proceso — perfecto para produccion (un solo storage por app), pero un test de integracion que
/// necesita apuntar a SU PROPIO Postgres efimero no puede mutar una variable global sin arriesgar pisarse con
/// otros tests que corren en paralelo. Inyectando el <see cref="JobStorage"/> como dependencia explicita, un
/// test puede armar el suyo (<c>new PostgreSqlStorage(connectionString)</c>) sin tocar el global. En
/// produccion, <c>Program.cs</c> sigue pasando <c>JobStorage.Current</c> (el mismo que ya configura
/// <c>AddHangfire</c>), asi que el comportamiento real no cambia.</para>
/// </summary>
public sealed class HangfireJobQueuePurgePort : IHangfireJobQueuePurgePort
{
    // Tope de paginas por cola: una purga post-restore no deberia encontrar miles de jobs colgados (el
    // mantenimiento dura minutos, no dias). Este tope es una defensa contra un bucle sin fin si el storage
    // devolviera datos raros; no es un limite de negocio. Si se alcanza, el resultado queda marcado
    // "incompleta" (ver HangfireJobPurgeStatus) en vez de mentir que se purgo todo.
    private const int PageSize = 100;
    private const int MaxPagesPerSource = 50; // hasta 5000 jobs por cola/por scheduled, de sobra

    private readonly JobStorage _jobStorage;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<HangfireJobQueuePurgePort> _logger;

    public HangfireJobQueuePurgePort(
        JobStorage jobStorage,
        IBackgroundJobClient backgroundJobClient,
        ILogger<HangfireJobQueuePurgePort> logger)
    {
        _jobStorage = jobStorage;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
    }

    /// <summary>Un job encontrado en alguna cola/programados, ya clasificado, esperando a que se borre.</summary>
    private readonly record struct JobToPurge(string JobId, string Category);

    public Task<HangfireJobPurgeResult> PurgeQueuedAndScheduledJobsAsync(CancellationToken ct = default)
    {
        // Hangfire no ofrece una API asincronica para esto (es una libreria vieja, sincronica por dentro);
        // el caller (SystemDataRestoreService) ya lo llama desde un contexto async, asi que devolvemos
        // Task.FromResult en vez de forzar un Task.Run innecesario.
        try
        {
            var monitoringApi = _jobStorage.GetMonitoringApi();
            var jobsToPurge = new List<JobToPurge>();

            // Si CUALQUIERA de las dos colecciones se corta por el tope de paginacion, el resultado final
            // queda marcado "incompleta" mas abajo: puede haber quedado algo sin recorrer.
            var seCortoPorTopeDePaginacion = false;
            seCortoPorTopeDePaginacion |= CollectEnqueuedJobs(monitoringApi, jobsToPurge, ct);
            seCortoPorTopeDePaginacion |= CollectScheduledJobs(monitoringApi, jobsToPurge, ct);

            var emisiones = 0;
            var anulaciones = 0;
            var otras = 0;
            var fallaAlBorrarAlgunJob = false;

            foreach (var job in jobsToPurge)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Delete() es idempotente: si el job ya no existe o ya esta en un estado final, no rompe.
                    _backgroundJobClient.Delete(job.JobId);
                    switch (job.Category)
                    {
                        case HangfireJobCategory.EmisionDeComprobante: emisiones++; break;
                        case HangfireJobCategory.AnulacionDeComprobante: anulaciones++; break;
                        default: otras++; break;
                    }
                }
                catch (Exception ex)
                {
                    // Un job puntual que no se pudo borrar no aborta la purga entera: mejor borrar 9 de 10 que
                    // ninguno. Pero SI queda registrado que la purga no fue 100% completa (T-8/PR-12).
                    fallaAlBorrarAlgunJob = true;
                    _logger.LogWarning(ex,
                        "Restaurar TOTAL: no se pudo purgar un job de Hangfire (JobId omitido del log por ruido, " +
                        "no es informacion sensible pero no aporta). Se sigue con el resto.");
                }
            }

            var estado = seCortoPorTopeDePaginacion || fallaAlBorrarAlgunJob
                ? HangfireJobPurgeStatus.Incompleta
                : HangfireJobPurgeStatus.Completa;

            return Task.FromResult(new HangfireJobPurgeResult(emisiones, anulaciones, otras, estado));
        }
        catch (Exception ex)
        {
            // El storage de Hangfire ni siquiera respondio (ej. no se pudo conectar). Antes esto devolvia 0
            // silenciosamente — indistinguible de "no habia nada para purgar". Ahora queda marcado "fallo" para
            // que la auditoria cuente la verdad (bloqueante de seguridad del retomo 2026-08-03).
            _logger.LogError(ex, "Restaurar TOTAL: la purga de colas de Hangfire fallo por completo (el storage no respondio).");
            return Task.FromResult(HangfireJobPurgeResult.Fallida());
        }
    }

    /// <returns>true si se corto por el tope de paginacion en ALGUNA cola (puede haber quedado jobs sin recorrer).</returns>
    private static bool CollectEnqueuedJobs(IMonitoringApi monitoringApi, List<JobToPurge> jobs, CancellationToken ct)
    {
        var seCorto = false;
        foreach (var queue in monitoringApi.Queues())
        {
            for (var page = 0; page < MaxPagesPerSource; page++)
            {
                ct.ThrowIfCancellationRequested();
                var batch = monitoringApi.EnqueuedJobs(queue.Name, page * PageSize, PageSize);
                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var entry in batch)
                {
                    jobs.Add(new JobToPurge(entry.Key, ClassifyJob(entry.Value.Job)));
                }

                if (batch.Count < PageSize)
                {
                    break; // ultima pagina de esta cola
                }

                if (page == MaxPagesPerSource - 1)
                {
                    seCorto = true; // llegamos al tope sin ver la ultima pagina: puede haber mas
                }
            }
        }

        return seCorto;
    }

    /// <returns>true si se corto por el tope de paginacion (puede haber quedado programados sin recorrer).</returns>
    private static bool CollectScheduledJobs(IMonitoringApi monitoringApi, List<JobToPurge> jobs, CancellationToken ct)
    {
        for (var page = 0; page < MaxPagesPerSource; page++)
        {
            ct.ThrowIfCancellationRequested();
            var batch = monitoringApi.ScheduledJobs(page * PageSize, PageSize);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var entry in batch)
            {
                jobs.Add(new JobToPurge(entry.Key, ClassifyJob(entry.Value.Job)));
            }

            if (batch.Count < PageSize)
            {
                break;
            }

            if (page == MaxPagesPerSource - 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Un job puede llegar con <c>Job = null</c> cuando Hangfire no pudo reconstruirlo (ej. referencia un tipo
    /// o metodo que ya no existe en el ensamblado actual — <c>LoadException</c> queda seteado en ese caso). Se
    /// clasifica como "otras tareas" por seguridad: nunca asumimos que un job irreconstruible es fiscal.
    /// </summary>
    private static string ClassifyJob(Hangfire.Common.Job? job)
        => HangfireJobCategoryClassifier.Classify(job?.Method?.Name);
}
