using System;
using NzbDrone.Common.Http;

namespace NzbDrone.Common.Cloud
{
    public interface ILidarrCloudRequestBuilder
    {
        IHttpRequestBuilderFactory Services { get; }
        IHttpRequestBuilderFactory Search { get; }
        IHttpRequestBuilderFactory InternalSearch { get; }
    }

    public class LidarrCloudRequestBuilder : ILidarrCloudRequestBuilder
    {
        // Default = public Servarr-hosted Lidarr Metadata Daemon (lmd). Override with
        // LIDARR_METADATA_URL to point at a local lmd mirror. The path "/{route}" is
        // a template the call sites fill in — the override should NOT include /{route},
        // just the base path up to and including /api/v0.4/ (or whatever prefix the
        // mirror exposes). Example for a local lmd:
        //
        //   LIDARR_METADATA_URL=http://10.30.0.159:5001/
        //
        // (Local lmd serves search/album/artist routes off the root, no /api/v0.4 prefix.)
        private const string DefaultSearchUrl = "https://api.lidarr.audio/api/v0.4/{route}";

        public LidarrCloudRequestBuilder()
        {
            Services = new HttpRequestBuilder("https://lidarr.servarr.com/v1/")
                .CreateFactory();

            var searchBase = Environment.GetEnvironmentVariable("LIDARR_METADATA_URL");
            if (string.IsNullOrWhiteSpace(searchBase))
            {
                searchBase = DefaultSearchUrl;
            }
            else
            {
                if (!searchBase.EndsWith("/"))
                {
                    searchBase += "/";
                }

                searchBase += "{route}";
            }

            Search = new HttpRequestBuilder(searchBase)
                .KeepAlive()
                .CreateFactory();
        }

        public IHttpRequestBuilderFactory Services { get; }

        public IHttpRequestBuilderFactory Search { get; }

        public IHttpRequestBuilderFactory InternalSearch { get; }
    }
}
