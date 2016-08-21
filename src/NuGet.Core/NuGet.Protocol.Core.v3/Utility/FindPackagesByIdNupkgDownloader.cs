// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;

namespace NuGet.Protocol
{
    public class FindPackagesByIdNupkgDownloader
    {
        private readonly object _nupkgCacheLock = new object();

        private readonly Dictionary<string, Task<HttpSourceResult>> _nupkgCache =
            new Dictionary<string, Task<HttpSourceResult>>();

        private readonly HttpSource _httpSource;
        private readonly ILogger _logger;

        public FindPackagesByIdNupkgDownloader(HttpSource httpSource, ILogger logger)
        {
            if (httpSource == null)
            {
                throw new ArgumentNullException(nameof(httpSource));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            _httpSource = httpSource;
            _logger = logger;
        }

        public async Task<Stream> OpenNupkgStreamAsync(
            PackageIdentity identity,
            string url,
            SourceCacheContext cacheContext,
            CancellationToken cancellationToken)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            if (url == null)
            {
                throw new ArgumentNullException(nameof(url));
            }

            if (cacheContext == null)
            {
                throw new ArgumentNullException(nameof(cacheContext));
            }

            // Don't read from the in-memory cache if we are doing a direct download.
            if (cacheContext.DirectDownload)
            {
                var httpSourceResult = await OpenNupkgStreamAsyncCore(identity, url, cacheContext, cancellationToken);

                // If we get back a result from the cache, we can save it to the in-memory cache.
                lock (_nupkgCacheLock)
                {
                    if (httpSourceResult.Status == HttpSourceResultStatus.OpenedFromDisk &&
                        !_nupkgCache.ContainsKey(url))
                    {
                        _nupkgCache[url] = Task.FromResult(httpSourceResult);
                    }
                }

                return httpSourceResult.Stream;
            }

            Task<HttpSourceResult> task;

            lock (_nupkgCacheLock)
            {
                if (!_nupkgCache.TryGetValue(url, out task))
                {
                    task = OpenNupkgStreamAsyncCore(identity, url, cacheContext, cancellationToken);
                    _nupkgCache[url] = task;
                }
            }

            var result = await task;

            if (result == null ||
                result.Status != HttpSourceResultStatus.OpenedFromDisk)
            {
                return null;
            }

            // Acquire the lock on a file before we open it to prevent this process
            // from opening a file deleted by the logic in HttpSource.GetAsync() in another process
            return await ConcurrencyUtilities.ExecuteWithFileLockedAsync(
                result.CacheFileName,
                action: token =>
                {
                    return Task.FromResult(new FileStream(
                        result.CacheFileName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete));
                },
                token: cancellationToken);
        }

        private async Task<HttpSourceResult> OpenNupkgStreamAsyncCore(
            PackageIdentity identity,
            string url,
            SourceCacheContext cacheContext,
            CancellationToken cancellationToken)
        {
            for (var retry = 0; retry != 3; ++retry)
            {
                var httpSourceCacheContext = HttpSourceCacheContext.Create(cacheContext, retry);

                try
                {
                    return await _httpSource.GetAsync(
                        new HttpSourceCachedRequest(
                            url,
                            "nupkg_" + identity.Id + "." + identity.Version.ToNormalizedString(),
                            httpSourceCacheContext)
                        {
                            EnsureValidContents = stream => HttpStreamValidation.ValidateNupkg(url, stream)
                        },
                        httpSourceResult => Task.FromResult(httpSourceResult),
                        _logger,
                        cancellationToken);
                }
                catch (TaskCanceledException) when (retry < 2)
                {
                    // Requests can get cancelled if we got the data from elsewhere, no reason to warn.
                    string message = string.Format(CultureInfo.CurrentCulture, Strings.Log_CanceledNupkgDownload, url);

                    _logger.LogMinimal(message);
                }
                catch (Exception ex) when (retry < 2)
                {
                    var message = string.Format(CultureInfo.CurrentCulture, Strings.Log_FailedToDownloadPackage, url)
                        + Environment.NewLine
                        + ExceptionUtilities.DisplayMessage(ex);

                    _logger.LogMinimal(message);
                }
                catch (Exception ex) when (retry == 2)
                {
                    var message = string.Format(CultureInfo.CurrentCulture, Strings.Log_FailedToDownloadPackage, url)
                        + Environment.NewLine
                        + ExceptionUtilities.DisplayMessage(ex);

                    _logger.LogError(message);
                }
            }

            return null;
        }
    }
}
