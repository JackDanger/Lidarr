using System.Collections.Generic;
using System.Threading.Tasks;
using NzbDrone.Core.Music;

namespace NzbDrone.Core.MetadataSource
{
    public interface ISearchForNewAlbum
    {
        List<Album> SearchForNewAlbum(string title, string artist);
        List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds);

        Task<List<Album>> SearchForNewAlbumAsync(string title, string artist) =>
            Task.FromResult(SearchForNewAlbum(title, artist));

        Task<List<Album>> SearchForNewAlbumByRecordingIdsAsync(List<string> recordingIds) =>
            Task.FromResult(SearchForNewAlbumByRecordingIds(recordingIds));

        Task<List<Album>> EnhancedSearchWithVariantsAsync(string title, string artist) =>
            SearchForNewAlbumAsync(title, artist);
    }
}
