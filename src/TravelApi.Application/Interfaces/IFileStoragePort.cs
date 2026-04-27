namespace TravelApi.Application.Interfaces;

public record StoredFileDescriptor(
    string StoredFileName,
    string FileName,
    string ContentType,
    long FileSize);

public interface IFileStoragePort
{
    Task<StoredFileDescriptor> SaveAsync(
        Stream stream,
        string objectName,
        string fileName,
        string contentType,
        CancellationToken cancellationToken);

    Task<(byte[] Bytes, string ContentType, string FileName)> GetAsync(
        string storedFileName,
        string fileName,
        string contentType,
        CancellationToken cancellationToken);

    Task DeleteAsync(string storedFileName, CancellationToken cancellationToken);
}
