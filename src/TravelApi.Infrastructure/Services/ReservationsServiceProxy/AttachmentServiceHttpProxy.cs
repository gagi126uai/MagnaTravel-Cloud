using System.Net.Http.Headers;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services.ReservationsServiceProxy;

public class AttachmentServiceHttpProxy : ReservationsServiceHttpProxyBase, IAttachmentService
{
    public AttachmentServiceHttpProxy(HttpClient httpClient)
        : base(httpClient)
    {
    }

    public Task<IEnumerable<AttachmentDto>> GetAttachmentsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct)
        => GetAsync<IEnumerable<AttachmentDto>>($"api/attachments/reserva/{reservaPublicIdOrLegacyId}", ct);

    public async Task<AttachmentDto> UploadAttachmentAsync(string reservaPublicIdOrLegacyId, Stream stream, string fileName, string contentType, string uploadedBy, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);

        return await PostMultipartAsync<AttachmentDto>($"api/attachments/upload/{reservaPublicIdOrLegacyId}", content, ct);
    }

    public Task<(byte[] Bytes, string ContentType, string FileName)> DownloadAttachmentAsync(string attachmentPublicIdOrLegacyId, CancellationToken ct)
        => GetFileAsync($"api/attachments/{attachmentPublicIdOrLegacyId}/download", ct);

    public Task DeleteAttachmentAsync(string attachmentPublicIdOrLegacyId, CancellationToken ct)
        => DeleteAsync($"api/attachments/{attachmentPublicIdOrLegacyId}", ct);
}
