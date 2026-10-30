using Exhume.Core;

namespace Exhume.Adapters.Git;

public interface IGitEvidenceReader : IEvidenceAdapter
{
    Task<PersonaInput> BuildPersonaInputAsync(ScanOptions options, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DiffChunk>> ReadDiffAsync(ScanOptions options, CancellationToken cancellationToken = default);
}
