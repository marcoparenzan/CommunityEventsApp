using System.Security.Claims;
using EventsCommon.Models;

namespace SpeakerApp.Services;

public interface ISpeakerAuthenticationService
{
    Task RequestPinAsync(string? email, string? loginBaseUrl = null, CancellationToken cancellationToken = default);
    Task<ClaimsPrincipal?> VerifyPinAsync(string? email, string? pin, CancellationToken cancellationToken = default);
    Task<SpeakerAccountResponse?> GetAccountAsync(int speakerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SpeakerSessionResponse>> GetSessionsAsync(int speakerId, CancellationToken cancellationToken = default);
    Task<bool> UpdateSessionLinksAsync(
        int speakerId,
        UpdateSpeakerSessionLinksRequest request,
        CancellationToken cancellationToken = default);
}
