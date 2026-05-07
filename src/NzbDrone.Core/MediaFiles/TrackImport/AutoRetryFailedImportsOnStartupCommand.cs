using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaFiles.TrackImport
{
    public class AutoRetryFailedImportsOnStartupCommand : Command
    {
        public override string CompletionMessage => "Auto-retry of failed imports completed";
        public override bool IsLongRunning => true;
    }
}
