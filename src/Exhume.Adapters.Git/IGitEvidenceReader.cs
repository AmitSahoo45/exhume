using Exhume.Core;

namespace Exhume.Adapters.Git;

public interface IGitEvidenceReader : IEvidenceAdapter
{
    PersonaContext GetPersonaContext(ScanOptions options);
    Task<PersonaInput> BuildPersonaInputAsync(ScanOptions options, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DiffChunk>> ReadDiffAsync(ScanOptions options, CancellationToken cancellationToken = default);
}
