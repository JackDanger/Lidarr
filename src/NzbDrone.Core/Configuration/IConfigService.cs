using System.Collections.Generic;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Security;

namespace NzbDrone.Core.Configuration
{
    public interface IConfigService
    {
        void SaveConfigDictionary(Dictionary<string, object> configValues);

        bool IsDefined(string key);

        // Download Client
        string DownloadClientWorkingFolders { get; set; }
        int DownloadClientHistoryLimit { get; set; }

        // Completed/Failed Download Handling (Download client)
        bool EnableCompletedDownloadHandling { get; set; }

        bool AutoRedownloadFailed { get; set; }
        bool AutoRedownloadFailedFromInteractiveSearch { get; set; }

        // Media Management
        bool AutoUnmonitorPreviouslyDownloadedTracks { get; set; }
        string RecycleBin { get; set; }
        int RecycleBinCleanupDays { get; set; }
        ProperDownloadTypes DownloadPropersAndRepacks { get; set; }
        bool CreateEmptyArtistFolders { get; set; }
        bool DeleteEmptyFolders { get; set; }
        FileDateType FileDate { get; set; }
        bool SkipFreeSpaceCheckWhenImporting { get; set; }
        int MinimumFreeSpaceWhenImporting { get; set; }
        bool CopyUsingHardlinks { get; set; }
        bool EnableMediaInfo { get; set; }
        bool UseScriptImport { get; set; }
        string ScriptImportPath { get; set; }
        bool ImportExtraFiles { get; set; }
        string ExtraFileExtensions { get; set; }

        // Orphan import (this fork): when an auto-import can't match audio
        // against any MusicBrainz release for the parsed artist, optionally
        // file the bits into the artist folder anyway so they aren't lost.
        // See CompletedDownloadService.TryOrphanImport.
        bool OrphanImportEnabled { get; set; }
        string OrphanImportSubfolder { get; set; }
        bool OrphanImportWriteMarker { get; set; }

        // When true (default), the import pipeline consults ImportListExclusion
        // before importing — files whose tags resolve to an excluded MBID get
        // terminal-failed (blocklist + remove from download client) the same
        // way an extracted-but-empty .iso does. Off = legacy behaviour where
        // exclusion only applies to import lists, not to downloaded content.
        bool RespectExclusionsOnImport { get; set; }

        // When true (default), a completed download that holds only video files
        // (no importable audio) is terminal-failed: blocklisted, removed from the
        // download client, and not re-grabbed. Off = park it as ImportBlocked.
        bool DeleteVideoOnlyDownloads { get; set; }

        // Minimum match score [0..1] for the Manual Import modal to surface a
        // MusicBrainz suggestion. 1.0 disables the feature; lower numbers are
        // noisier. Tuned by the user via commits on this branch.
        decimal ManualImportSuggestionThreshold { get; set; }

        bool WatchLibraryForChanges { get; set; }
        RescanAfterRefreshType RescanAfterRefresh { get; set; }
        AllowFingerprinting AllowFingerprinting { get; set; }

        // Permissions (Media Management)
        bool SetPermissionsLinux { get; set; }
        string ChmodFolder { get; set; }
        string ChownGroup { get; set; }

        // Indexers
        int Retention { get; set; }
        int RssSyncInterval { get; set; }
        int MaximumSize { get; set; }
        int MinimumAge { get; set; }

        // UI
        int FirstDayOfWeek { get; set; }
        string CalendarWeekColumnHeader { get; set; }

        string ShortDateFormat { get; set; }
        string LongDateFormat { get; set; }
        string TimeFormat { get; set; }
        bool ShowRelativeDates { get; set; }
        bool EnableColorImpairedMode { get; set; }
        int UILanguage { get; set; }

        bool ExpandAlbumByDefault { get; set; }
        bool ExpandSingleByDefault { get; set; }
        bool ExpandEPByDefault { get; set; }
        bool ExpandBroadcastByDefault { get; set; }
        bool ExpandOtherByDefault { get; set; }

        // Internal
        bool CleanupMetadataImages { get; set; }

        string PlexClientIdentifier { get; }

        // Metadata
        string MetadataSource { get; set; }
        WriteAudioTagsType WriteAudioTags { get; set; }
        bool ScrubAudioTags { get; set; }
        bool EmbedCoverArt { get; set; }

        // Forms Auth
        string RijndaelPassphrase { get; }
        string HmacPassphrase { get; }
        string RijndaelSalt { get; }
        string HmacSalt { get; }

        // Proxy
        bool ProxyEnabled { get; }
        ProxyType ProxyType { get; }
        string ProxyHostname { get; }
        int ProxyPort { get; }
        string ProxyUsername { get; }
        string ProxyPassword { get; }
        string ProxyBypassFilter { get; }
        bool ProxyBypassLocalAddresses { get; }

        // Backups
        string BackupFolder { get; }
        int BackupInterval { get; }
        int BackupRetention { get; }

        CertificateValidationType CertificateValidation { get; }
        string ApplicationUrl { get; }
    }
}
