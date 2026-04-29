using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

public class MinioFileStoragePort : IFileStoragePort
{
    private readonly IMinioClient _minioClient;
    private readonly ILogger<MinioFileStoragePort> _logger;
    private readonly string _bucketName;

    public MinioFileStoragePort(
        IMinioClient minioClient,
        IConfiguration configuration,
        ILogger<MinioFileStoragePort> logger)
    {
        _minioClient = minioClient;
        _logger = logger;
        _bucketName = configuration["Minio:BucketName"] ?? "reservations";
    }

    public async Task<StoredFileDescriptor> SaveAsync(
        Stream stream,
        string objectName,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length == 0)
        {
            throw new InvalidOperationException("El archivo esta vacio.");
        }

        try
        {
            buffer.Position = 0;
            var args = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectName.Replace('\\', '/'))
                .WithStreamData(buffer)
                .WithObjectSize(buffer.Length)
                .WithContentType(contentType);

            await _minioClient.PutObjectAsync(args, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading object {ObjectName} to MinIO. Bucket: {Bucket}", objectName, _bucketName);
            
            // Si es un error de comunicación, queremos saberlo.
            var detail = ex is MinioException ? "Error de protocolo MinIO" : "Error de red o conexión con el almacenamiento";
            throw new InvalidOperationException($"{detail}: {ex.Message}");
        }

        return new StoredFileDescriptor(objectName.Replace('\\', '/'), fileName, contentType, buffer.Length);
    }

    public async Task<(byte[] Bytes, string ContentType, string FileName)> GetAsync(
        string storedFileName,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        try
        {
            using var memoryStream = new MemoryStream();
            var args = new GetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(storedFileName.Replace('\\', '/'))
                .WithCallbackStream(stream => stream.CopyTo(memoryStream));

            await _minioClient.GetObjectAsync(args, cancellationToken);
            return (memoryStream.ToArray(), contentType, fileName);
        }
        catch (MinioException ex)
        {
            _logger.LogError(ex, "Error downloading object {ObjectName} from MinIO.", storedFileName);
            throw new FileNotFoundException("Archivo no encontrado en el almacenamiento remoto.");
        }
    }

    public async Task DeleteAsync(string storedFileName, CancellationToken cancellationToken)
    {
        try
        {
            var args = new RemoveObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(storedFileName.Replace('\\', '/'));

            await _minioClient.RemoveObjectAsync(args, cancellationToken);
        }
        catch (MinioException ex)
        {
            _logger.LogError(ex, "Error deleting object {ObjectName} from MinIO.", storedFileName);
        }
    }
}
