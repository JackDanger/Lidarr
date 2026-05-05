using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(081)]
    public class add_trackfileid_index : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Add indexes to speed up AlbumsWithoutFiles query which uses EXISTS subquery
            // Use IF NOT EXISTS to handle cases where indexes already exist

            // Index on Tracks.TrackFileId for checking if track has a file
            IfDatabase("postgres").Execute.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_Tracks_TrackFileId"" ON ""Tracks"" (""TrackFileId"")");
            IfDatabase("sqlite").Execute.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_Tracks_TrackFileId"" ON ""Tracks"" (""TrackFileId"")");

            // Index on AlbumReleases.AlbumId for subquery join (ar.AlbumId = Albums.Id)
            IfDatabase("postgres").Execute.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_AlbumReleases_AlbumId"" ON ""AlbumReleases"" (""AlbumId"")");
            IfDatabase("sqlite").Execute.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_AlbumReleases_AlbumId"" ON ""AlbumReleases"" (""AlbumId"")");

            // Composite index on Albums for the main query filters
            IfDatabase("postgres").Execute.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_Albums_Monitored_ReleaseDate"" ON ""Albums"" (""Monitored"", ""ReleaseDate"")");
            IfDatabase("sqlite").Execute.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_Albums_Monitored_ReleaseDate"" ON ""Albums"" (""Monitored"", ""ReleaseDate"")");
        }
    }
}
