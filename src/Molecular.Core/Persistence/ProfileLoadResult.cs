using Molecular.Core.Models;

namespace Molecular.Core.Persistence;

public sealed record ProfileLoadResult(
    ProfileCatalog Catalog,
    bool RecoveredFromBackup,
    bool ResetToDefault,
    string? Notice)
{
    public MixerProfile Profile => Catalog.ActiveProfile;
}
