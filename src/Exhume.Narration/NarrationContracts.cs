using Exhume.Core;

namespace Exhume.Narration;

public sealed record Obituary(string Headline, string Body, string CauseOfDeath, string Epitaph);

public sealed record EraSummary(string Title, string Body, IReadOnlyList<string> Themes);

public sealed record Persona(
    string VoiceDescription,
    IReadOnlyList<string> Habits,
    IReadOnlyList<string> PetPhrases,
    IReadOnlyList<string> ThingsToNitpick,
    string TechnicalWorldview);

public sealed record HauntComment(string File, int? LineNumber, string Comment);

public abstract record NarrationResult<T>;

public sealed record NarrationSuccess<T>(T Value, bool FromCache = false) : NarrationResult<T>;

public sealed record NarrationFailure<T>(string Reason) : NarrationResult<T>;

public interface INarrationService
{
    int CallCount { get; }

    Task<NarrationResult<Obituary>> NarrateObituaryAsync(
        Artifact artifact,
        CancellationToken cancellationToken = default);

    Task<NarrationResult<EraSummary>> NarrateEraAsync(
        IReadOnlyList<Artifact> artifacts,
        CancellationToken cancellationToken = default);

    Task<NarrationResult<Persona>> BuildPersonaAsync(
        PersonaInput input,
        CancellationToken cancellationToken = default);

    Task<NarrationResult<IReadOnlyList<HauntComment>>> HauntAsync(
        Persona persona,
        DiffChunk chunk,
        CancellationToken cancellationToken = default);
}
