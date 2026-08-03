using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Arreglo 2f del retomo 2026-08-03 ("limpiar y avisar"): cubre <see cref="HangfireJobQueuePurgePort"/> con
/// <see cref="Mock{JobStorage}"/>/<see cref="Mock{IMonitoringApi}"/> — el refactor que inyecta
/// <c>JobStorage</c> por constructor (en vez de leer <c>JobStorage.Current</c> adentro del metodo) es lo que
/// hace esto posible SIN levantar un Postgres real ni tocar el storage global del proceso.
///
/// <para><b>Por que no hay un test de integracion con Hangfire+Postgres real</b> (nota honesta pedida por el
/// brief): <c>Hangfire.PostgreSql</c> instala su PROPIO esquema (tablas <c>hangfire.job</c>, <c>hangfire.set</c>,
/// etc.) que ningun fixture de este repo crea hoy — ni <see cref="TravelApi.Tests.Fixtures.PostgresIntegrationFixture"/>
/// (que arma el esquema de la app con <c>EnsureCreatedAsync</c>) ni <c>CustomWebApplicationFactory</c> (usa
/// InMemory). Armar ese esquema a mano solo para este test duplicaria — y podria desincronizarse de — la
/// migracion/instalacion real de <c>Hangfire.PostgreSql</c>, y el AUTOR de esa libreria ya la testea contra su
/// propio esquema. Estos tests de aca SI cubren la logica que es NUESTRA (clasificacion, desglose, pagina de
/// tope, estado de la purga) con dobles fieles a los CONTRATOS reales de Hangfire (<see cref="IMonitoringApi"/>
/// es una interfaz real, no un fake nuestro) — es la cobertura de mayor valor con el esfuerzo disponible.</para>
/// </summary>
public class HangfireJobQueuePurgePortTests
{
    private static MethodInfo ProcessInvoiceJobMethod
        => typeof(IAfipService).GetMethod(nameof(IAfipService.ProcessInvoiceJob))!;

    private static MethodInfo ProcessAnnulmentJobMethod
        => typeof(IInvoiceService).GetMethod(nameof(IInvoiceService.ProcessAnnulmentJob))!;

    /// <summary>
    /// Arma un <see cref="Job"/> real (no un fake) para un metodo con parametros. Hangfire valida que la
    /// cantidad de argumentos matchee EXACTO la firma del metodo (no acepta "faltan, hay defaults") — por eso
    /// se arma un argumento dummy por cada parametro (0 para <c>int</c>, <c>null</c> para el resto).
    /// </summary>
    private static Job JobFor(MethodInfo method)
    {
        var dummyArgs = method.GetParameters()
            .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray();
        return new Job(method.DeclaringType!, method, dummyArgs);
    }

    private static HangfireJobQueuePurgePort NewPort(
        Mock<JobStorage> jobStorageMock, Mock<IBackgroundJobClient> backgroundJobClientMock)
        => new(jobStorageMock.Object, backgroundJobClientMock.Object, NullLogger<HangfireJobQueuePurgePort>.Instance);

    /// <summary>Arma un storage mock con UNA cola ("default") y CERO programados, listo para que cada test
    /// sobrescriba <c>EnqueuedJobs</c> con los jobs que le interesan.</summary>
    private static (Mock<JobStorage> Storage, Mock<IMonitoringApi> Monitoring) NewMonitoringApiMock()
    {
        var monitoringApiMock = new Mock<IMonitoringApi>();
        monitoringApiMock.Setup(m => m.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto>
        {
            new() { Name = "default" },
        });
        monitoringApiMock
            .Setup(m => m.ScheduledJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new JobList<ScheduledJobDto>(Array.Empty<KeyValuePair<string, ScheduledJobDto>>()));

        var jobStorageMock = new Mock<JobStorage>();
        jobStorageMock.Setup(s => s.GetMonitoringApi()).Returns(monitoringApiMock.Object);
        return (jobStorageMock, monitoringApiMock);
    }

    [Fact]
    public async Task PurgeQueuedAndScheduledJobsAsync_ClasificaEmisionAnulacionYOtras_BorraLasTresYCuentaCadaUna()
    {
        var (jobStorageMock, monitoringApiMock) = NewMonitoringApiMock();
        monitoringApiMock
            .Setup(m => m.EnqueuedJobs("default", 0, It.IsAny<int>()))
            .Returns(new JobList<EnqueuedJobDto>(new[]
            {
                new KeyValuePair<string, EnqueuedJobDto>("job-emision", new EnqueuedJobDto { Job = JobFor(ProcessInvoiceJobMethod) }),
                new KeyValuePair<string, EnqueuedJobDto>("job-anulacion", new EnqueuedJobDto { Job = JobFor(ProcessAnnulmentJobMethod) }),
                new KeyValuePair<string, EnqueuedJobDto>("job-otro", new EnqueuedJobDto { Job = null }),
            }));

        var backgroundJobClientMock = new Mock<IBackgroundJobClient>();
        var port = NewPort(jobStorageMock, backgroundJobClientMock);

        var result = await port.PurgeQueuedAndScheduledJobsAsync();

        Assert.Equal(1, result.EmisionesDeComprobantePurgadas);
        Assert.Equal(1, result.AnulacionesDeComprobantePurgadas);
        Assert.Equal(1, result.OtrasTareasPurgadas);
        Assert.Equal(HangfireJobPurgeStatus.Completa, result.Estado);
        // Delete() es un metodo de EXTENSION que por dentro llama a ChangeState con un DeletedState —
        // verificamos que los 3 jobs se intentaron borrar, no solo que se contaron.
        backgroundJobClientMock.Verify(
            c => c.ChangeState(It.IsIn("job-emision", "job-anulacion", "job-otro"), It.IsAny<DeletedState>(), It.IsAny<string>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task PurgeQueuedAndScheduledJobsAsync_UnJobNoSePuedeBorrar_QuedaIncompletaYLosDemasSeCuentanIgual()
    {
        var (jobStorageMock, monitoringApiMock) = NewMonitoringApiMock();
        monitoringApiMock
            .Setup(m => m.EnqueuedJobs("default", 0, It.IsAny<int>()))
            .Returns(new JobList<EnqueuedJobDto>(new[]
            {
                new KeyValuePair<string, EnqueuedJobDto>("job-ok", new EnqueuedJobDto { Job = JobFor(ProcessInvoiceJobMethod) }),
                new KeyValuePair<string, EnqueuedJobDto>("job-roto", new EnqueuedJobDto { Job = JobFor(ProcessInvoiceJobMethod) }),
            }));

        var backgroundJobClientMock = new Mock<IBackgroundJobClient>();
        backgroundJobClientMock
            .Setup(c => c.ChangeState("job-roto", It.IsAny<DeletedState>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("el storage rechazo este job puntual"));

        var port = NewPort(jobStorageMock, backgroundJobClientMock);

        var result = await port.PurgeQueuedAndScheduledJobsAsync();

        // Bloqueante de seguridad (2026-08-03): un job que no se pudo borrar NO se cuenta como purgado, pero
        // TAMPOCO se abandona el resto — se sigue con los demas (mejor borrar 1 de 2 que ninguno).
        Assert.Equal(1, result.EmisionesDeComprobantePurgadas);
        Assert.Equal(HangfireJobPurgeStatus.Incompleta, result.Estado);
    }

    [Fact]
    public async Task PurgeQueuedAndScheduledJobsAsync_ElStorageNoResponde_DevuelveEstadoFalloSinTirar()
    {
        var jobStorageMock = new Mock<JobStorage>();
        jobStorageMock.Setup(s => s.GetMonitoringApi()).Throws(new InvalidOperationException("no se pudo conectar"));
        var backgroundJobClientMock = new Mock<IBackgroundJobClient>();
        var port = NewPort(jobStorageMock, backgroundJobClientMock);

        var result = await port.PurgeQueuedAndScheduledJobsAsync();

        // Antes de este fix, un catch devolvia 0 — INDISTINGUIBLE de "no habia nada para purgar". Ahora el
        // estado "fallo" cuenta la verdad (bloqueante de seguridad, retomo 2026-08-03).
        Assert.Equal(0, result.Total);
        Assert.Equal(HangfireJobPurgeStatus.Fallo, result.Estado);
        backgroundJobClientMock.Verify(
            c => c.ChangeState(It.IsAny<string>(), It.IsAny<DeletedState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task PurgeQueuedAndScheduledJobsAsync_MasDe5000JobsEnUnaCola_SeCortaPorElTopeYQuedaIncompleta()
    {
        // MaxPagesPerSource=50 * PageSize=100 (constantes privadas del port): si UNA cola sigue devolviendo
        // paginas completas de 100 hasta la ultima permitida, el port se corta ahi (defensa contra un bucle
        // sin fin) y el resultado NUNCA puede decir "completa" — puede haber quedado jobs sin recorrer.
        var (jobStorageMock, monitoringApiMock) = NewMonitoringApiMock();
        monitoringApiMock
            .Setup(m => m.EnqueuedJobs("default", It.IsAny<int>(), It.IsAny<int>()))
            .Returns(() => new JobList<EnqueuedJobDto>(BuildFullPageOfOtrasTareas(100)));

        var backgroundJobClientMock = new Mock<IBackgroundJobClient>();
        var port = NewPort(jobStorageMock, backgroundJobClientMock);

        var result = await port.PurgeQueuedAndScheduledJobsAsync();

        Assert.Equal(HangfireJobPurgeStatus.Incompleta, result.Estado);
        Assert.Equal(5000, result.OtrasTareasPurgadas); // 50 paginas x 100 = el tope exacto que el port recorre
    }

    /// <summary>
    /// Hallazgo de seguridad (re-review, 2026-08-03): todos los tests de arriba dejan <c>ScheduledJobs</c>
    /// vacio (ver <see cref="NewMonitoringApiMock"/>), asi que esa rama del port corria SIEMPRE sobre una
    /// lista vacia — nunca se probo con un programado real. Este test pone un job DE VERDAD ahi.
    /// </summary>
    [Fact]
    public async Task PurgeQueuedAndScheduledJobsAsync_JobProgramadoDeVerdad_SeBorraYSeClasifica()
    {
        var (jobStorageMock, monitoringApiMock) = NewMonitoringApiMock();
        monitoringApiMock
            .Setup(m => m.EnqueuedJobs("default", 0, It.IsAny<int>()))
            .Returns(new JobList<EnqueuedJobDto>(Array.Empty<KeyValuePair<string, EnqueuedJobDto>>()));
        monitoringApiMock
            .Setup(m => m.ScheduledJobs(0, It.IsAny<int>()))
            .Returns(new JobList<ScheduledJobDto>(new[]
            {
                new KeyValuePair<string, ScheduledJobDto>(
                    "job-programado-emision", new ScheduledJobDto { Job = JobFor(ProcessInvoiceJobMethod) }),
            }));

        var backgroundJobClientMock = new Mock<IBackgroundJobClient>();
        var port = NewPort(jobStorageMock, backgroundJobClientMock);

        var result = await port.PurgeQueuedAndScheduledJobsAsync();

        Assert.Equal(1, result.EmisionesDeComprobantePurgadas);
        Assert.Equal(HangfireJobPurgeStatus.Completa, result.Estado);
        backgroundJobClientMock.Verify(
            c => c.ChangeState("job-programado-emision", It.IsAny<DeletedState>(), It.IsAny<string>()), Times.Once);
    }

    /// <summary>
    /// Garantia "no toca recurrentes por construccion" (por diseno, no por casualidad): el port SOLO recorre
    /// colas y programados (<see cref="IMonitoringApi.Queues"/>/<see cref="IMonitoringApi.ScheduledJobs"/>).
    /// <c>JobStorage.GetConnection()</c> es el camino que usaria para tocar los RECURRENTES (ej.
    /// <c>IStorageConnection.GetRecurringJobs</c>) — si el port alguna vez lo llamara, seria una señal de que
    /// alguien agrego una ruta que puede alcanzar a los recurrentes de sistema, justo lo que la obra promete
    /// que nunca pasa.
    /// </summary>
    [Fact]
    public async Task PurgeQueuedAndScheduledJobsAsync_NuncaLlamaGetConnection()
    {
        var (jobStorageMock, monitoringApiMock) = NewMonitoringApiMock();
        monitoringApiMock
            .Setup(m => m.EnqueuedJobs("default", 0, It.IsAny<int>()))
            .Returns(new JobList<EnqueuedJobDto>(Array.Empty<KeyValuePair<string, EnqueuedJobDto>>()));

        var backgroundJobClientMock = new Mock<IBackgroundJobClient>();
        var port = NewPort(jobStorageMock, backgroundJobClientMock);

        await port.PurgeQueuedAndScheduledJobsAsync();

        jobStorageMock.Verify(s => s.GetConnection(), Times.Never);
    }

    private static IEnumerable<KeyValuePair<string, EnqueuedJobDto>> BuildFullPageOfOtrasTareas(int cantidad)
    {
        for (var i = 0; i < cantidad; i++)
        {
            // JobId repetido a proposito entre paginas (el mock no distingue por pagina): el port solo
            // ACUMULA jobIds para borrar, no le importa si hay duplicados para este test de conteo.
            yield return new KeyValuePair<string, EnqueuedJobDto>($"job-{Guid.NewGuid()}", new EnqueuedJobDto { Job = null });
        }
    }
}

/// <summary>
/// Arreglo 2f: el mapeo nombre-de-metodo -> categoria de negocio es PURO (ni Hangfire ni base de datos), asi
/// que se testea directo sin ningun doble.
/// </summary>
public class HangfireJobCategoryClassifierTests
{
    [Fact]
    public void Classify_ProcessInvoiceJob_EsEmisionDeComprobante()
    {
        Assert.Equal(HangfireJobCategory.EmisionDeComprobante, HangfireJobCategoryClassifier.Classify("ProcessInvoiceJob"));
    }

    [Fact]
    public void Classify_ProcessAnnulmentJob_EsAnulacionDeComprobante()
    {
        Assert.Equal(HangfireJobCategory.AnulacionDeComprobante, HangfireJobCategoryClassifier.Classify("ProcessAnnulmentJob"));
    }

    /// <summary>
    /// Hallazgo de seguridad (re-review, 2026-08-03): una NC parcial (cancelacion parcial de un servicio ya
    /// facturado) es un comprobante que se postea a ARCA, igual que ProcessInvoiceJob. Antes de este fix caia
    /// en "OtrasTareas" y el aviso fiscal post-restore no se disparaba para este job.
    /// </summary>
    [Fact]
    public void Classify_ProcessPartialCreditNoteJob_EsEmisionDeComprobante()
    {
        Assert.Equal(HangfireJobCategory.EmisionDeComprobante, HangfireJobCategoryClassifier.Classify("ProcessPartialCreditNoteJob"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EnviarRecordatorioDeVencimiento")]
    [InlineData("processinvoicejob")] // distinto case: NO matchea (Ordinal a proposito, ver XML doc del helper)
    public void Classify_CualquierOtroNombre_EsOtrasTareas(string? jobMethodName)
    {
        Assert.Equal(HangfireJobCategory.OtrasTareas, HangfireJobCategoryClassifier.Classify(jobMethodName));
    }
}
