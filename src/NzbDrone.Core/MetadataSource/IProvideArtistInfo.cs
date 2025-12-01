using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NzbDrone.Core.Music;

namespace NzbDrone.Core.MetadataSource
{
    public interface IProvideArtistInfo
    {
        Artist GetArtistInfo(string lidarrId, int metadataProfileId);
        HashSet<string> GetChangedArtists(DateTime startTime);
    }

    public interface IProvideArtistInfoAsync : IProvideArtistInfo
    {
        Task<Artist> GetArtistInfoAsync(string lidarrId, int metadataProfileId);
        Task<HashSet<string>> GetChangedArtistsAsync(DateTime startTime);
    }
}
