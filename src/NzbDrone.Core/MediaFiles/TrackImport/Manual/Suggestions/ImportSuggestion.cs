namespace NzbDrone.Core.MediaFiles.TrackImport.Manual.Suggestions
{
    // A single MusicBrainz album/artist pair we propose as the right match for a
    // set of files that Lidarr couldn't auto-identify. Score is in [0, 1] where
    // 1 == perfect match against the file tags. Stays serializable: the API
    // surfaces it on ManualImportResource and the frontend renders it as a
    // one-click Accept option.
    public class ImportSuggestion
    {
        public string ArtistName { get; set; }
        public string ArtistMBID { get; set; }
        public string AlbumName { get; set; }
        public string AlbumMBID { get; set; }
        public double Score { get; set; }
        public int MbTrackCount { get; set; }
        public int LocalTrackCount { get; set; }
    }
}
