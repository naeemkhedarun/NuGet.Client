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

        private readonly Dictionary<string, Task<NupkgEntry>> _nupkgCache =
            new Dictionary<string, Task<NupkgEntry>>();

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

        public async Task<bool> CopyNupkgToStreamAsync(
            PackageIdentity identity,
            string url,
            Stream destination,
            SourceCacheContext cacheContext,
            CancellationToken token)
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

            if (cacheContext.DirectDownload)
            {
                // Don't read from the in-memory cache if we are doing a direct download.
                var nupkgEntry = await CopyNupkgToStreamCoreAsync(
                    identity,
                    url,
                    destination,
                    cacheContext,
                    token);

                // If we get back a cache file result from the cache, we can save it to the in-memory cache.
                lock (_nupkgCacheLock)
                {
                    if (nupkgEntry.CacheFile != null && !_nupkgCache.ContainsKey(url))
                    {
                        _nupkgCache[url] = Task.FromResult(nupkgEntry);
                    }
                }

                // Process the NupkgEntry
                return await CopyCacheFileToStreamAsync(nupkgEntry, destination, token);
            }
            else
            {
                // Try to get the NupkgEntry from the in-memory cache. If we find a match, we can open the cache file
                // and use that as the source stream, instead of going to the package source.
                Task<NupkgEntry> nupkgEntryTask;
                lock (_nupkgCacheLock)
                {
                    if (!_nupkgCache.TryGetValue(url, out nupkgEntryTask))
                    {
                        nupkgEntryTask = CopyNupkgToStreamCoreAsync(
                            identity,
                            url,
                            destination,
                            cacheContext,
                            token);

                        _nupkgCache[url] = nupkgEntryTask;
                    }
                }

                var nupkgEntry = await nupkgEntryTask;

                return await CopyCacheFileToStreamAsync(nupkgEntry, destination, token);
            }
        }

        private async Task<NupkgEntry> CopyNupkgToStreamCoreAsync(
            PackageIdentity identity,
            string url,
            Stream destination,
            SourceCacheContext cacheContext,
            CancellationToken token)
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
                        async httpSourceResult =>
                        {
                            if (httpSourceResult.Stream == null)
                            {
                                return new NupkgEntry(wasCopied: false, cacheFileName: null);
                            }

                            if (httpSourceResult.CacheFile != null)
                            {
                                // Return the cache file name so that the caller can open the cache file directly
                                // and copy it to the destination stream.
                                return new NupkgEntry(
                                    wasCopied: false,
                                    cacheFileName: httpSourceResult.CacheFile);
                            }
                            else
                            {
                                await httpSourceResult.Stream.CopyToAsync(destination, token);

                                // When the stream came from the network directly, there is not cache file name. This
                                // happens when the caller enables DirectDownload.
                                return new NupkgEntry(
                                    wasCopied: true,
                                    cacheFileName: null);
                            }
                        },
                        _logger,
                        token);
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

            return new NupkgEntry(wasCopied: false, cacheFileName: null);
        }

        private async Task<bool> CopyCacheFileToStreamAsync(
            NupkgEntry nupkgEntry,
            Stream destination,
            CancellationToken token)
        {
            if (nupkgEntry.WasCopied)
            {
                return true;
            }

            if (nupkgEntry.CacheFile == null)
            {
                return false;
            }

            // Acquire the lock on a file before we open it to prevent this process
            // from opening a file deleted by another HTTP request.
            using (var cacheStream = await ConcurrencyUtilities.ExecuteWithFileLockedAsync(
                nupkgEntry.CacheFile,
                lockedToken =>
                {
                    return Task.FromResult(new FileStream(
                        nupkgEntry.CacheFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        StreamExtensions.BufferSize,
                        useAsync: true));
                },
                token))
            {
                await cacheStream.CopyToAsync(destination, token);

                return true;
            }
        }

        private class NupkgEntry
        {
            public NupkgEntry(bool wasCopied, string cacheFileName)
            {
                WasCopied = wasCopied;
                CacheFile = cacheFileName;
            }

            public bool WasCopied { get; }
            public string CacheFile { get; }
        }
    }
}
