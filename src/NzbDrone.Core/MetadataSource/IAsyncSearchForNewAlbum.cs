using System.Collections.Generic;
using System.Threading.Tasks;
using NzbDrone.Core.Music;

namespace NzbDrone.Core.MetadataSource
{
    /// <summary>
    /// Async counterpart to <see cref="ISearchForNewAlbum"/>. Kept as a separate interface
    /// so out-of-tree plugins (notably Lidarr.Plugin.Tubifarry) that only implement the
    /// sync surface continue to load cleanly without their internal proxy generator
    /// emitting "missing required methods" warnings.
    ///
    /// Implementations of this interface enable concurrent album identification — see
    /// <see cref="MediaFiles.TrackImport.Identification.IdentificationService"/>. When no
    /// implementation is registered, identification falls back to the sync surface via
    /// Task.Run wrapping in CandidateService.
    /// </summary>
    public interface IAsyncSearchForNewAlbum
    {
        Task<List<Album>> SearchForNewAlbumAsync(string title, string artist);
        Task<List<Album>> SearchForNewAlbumByRecordingIdsAsync(List<string> recordingIds);
        Task<List<Album>> EnhancedSearchWithVariantsAsync(string title, string artist);
    }
}
