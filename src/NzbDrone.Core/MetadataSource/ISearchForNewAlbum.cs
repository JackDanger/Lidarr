using System.Collections.Generic;
using System.Threading.Tasks;
using NzbDrone.Core.Music;

namespace NzbDrone.Core.MetadataSource
{
    public interface ISearchForNewAlbum
    {
        List<Album> SearchForNewAlbum(string title, string artist);
        List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds);

        // Async variants are added as default interface methods so existing implementations
        // (notably out-of-tree plugins like Lidarr.Plugin.Tubifarry that implement only the
        // sync surface) keep loading without modification. SkyHookProxy overrides these with
        // genuinely-async implementations to enable concurrent identification.
        Task<List<Album>> SearchForNewAlbumAsync(string title, string artist) =>
            Task.FromResult(SearchForNewAlbum(title, artist));

        Task<List<Album>> SearchForNewAlbumByRecordingIdsAsync(List<string> recordingIds) =>
            Task.FromResult(SearchForNewAlbumByRecordingIds(recordingIds));

        Task<List<Album>> EnhancedSearchWithVariantsAsync(string title, string artist) =>
            SearchForNewAlbumAsync(title, artist);
    }
}
