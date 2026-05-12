using System.Collections.Generic;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.TrackImport.Manual.Suggestions
{
    public interface IImportSuggestionService
    {
        // Given a set of files whose identification has failed (or which haven't
        // been identified yet), look up MusicBrainz for a plausible match. Returns
        // null if nothing scores above the configured threshold OR if the only
        // candidates resolve to artists/albums the user has excluded.
        ImportSuggestion FindForTracks(IReadOnlyList<LocalTrack> localTracks);
    }
}
