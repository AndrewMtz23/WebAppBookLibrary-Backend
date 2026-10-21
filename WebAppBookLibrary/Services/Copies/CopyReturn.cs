using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebAppBookLibrary.Contracts.Copies;
using WebAppBookLibrary.Domain.Copies;

namespace WebAppBookLibrary.Services.Copies;

internal static class CopyReturn
{
    internal static bool Valid(ReturnPhysicalRequest? request) => request?.Copy is { ExpectedVersion: >0 } && request.Inspection is not null
        && CopyRules.ValidCondition(request.Inspection.Condition) && CopyRules.ValidNotes(request.Inspection.Notes)
        && (request.Inspection.Location?.Length ?? 0)<=200 && !string.IsNullOrWhiteSpace(request.IdempotencyKey) && request.IdempotencyKey.Length<=100;
    internal static string Fingerprint(ReturnPhysicalRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
}
