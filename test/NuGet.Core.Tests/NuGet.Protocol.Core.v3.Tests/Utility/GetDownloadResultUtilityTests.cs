﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Test.Utility;
using NuGet.Versioning;
using Test.Utility;
using Xunit;

namespace NuGet.Protocol.Tests
{
    public class GetDownloadResultUtilityTests
    {
        [Fact]
        public async Task GetDownloadResultUtility_WithSingleDirectDownload_ReturnsTemporaryDownloadResult()
        {
            // Arrange
            var uri = new Uri("http://fake/content.nupkg");
            var expectedContent = "TestContent";
            var identity = new PackageIdentity("PackageA", NuGetVersion.Parse("1.0.0-Beta"));
            var testHttpSource = new TestHttpSource(
                new PackageSource("http://fake"),
                new Dictionary<string, string>
                {
                    { uri.ToString(), expectedContent }
                });
            var logger = new TestLogger();
            var token = CancellationToken.None;

            using (var cacheContext = new SourceCacheContext())
            using (var downloadDirectory = TestFileSystemUtility.CreateRandomTestFolder())
            using (var globalPackagesFolder = TestFileSystemUtility.CreateRandomTestFolder())
            {
                var downloadContext = new PackageDownloadContext(
                    cacheContext,
                    downloadDirectory);

                // Act
                using (var result = await GetDownloadResultUtility.GetDownloadResultAsync(
                    testHttpSource,
                    identity,
                    uri,
                    downloadContext,
                    globalPackagesFolder,
                    logger,
                    token))
                {
                    // Assert
                    Assert.Null(result.PackageReader);
                    Assert.Equal(DownloadResourceResultStatus.Available, result.Status);
                    Assert.NotNull(result.PackageStream);
                    Assert.True(result.PackageStream.CanSeek);
                    Assert.True(result.PackageStream.CanRead);
                    Assert.Equal(0, result.PackageStream.Position);

                    var files = Directory.EnumerateFileSystemEntries(downloadDirectory).ToArray();
                    Assert.Equal(1, files.Length);
                    Assert.StartsWith("packagea.1.0.0-beta.", Path.GetFileName(files[0]));

                    Assert.Equal(0, Directory.EnumerateFileSystemEntries(globalPackagesFolder).Count());

                    var actualContent = new StreamReader(result.PackageStream).ReadToEnd();
                    Assert.Equal(expectedContent, actualContent);
                }

                Assert.Equal(0, Directory.EnumerateFileSystemEntries(downloadDirectory).Count());
                Assert.Equal(0, Directory.EnumerateFileSystemEntries(globalPackagesFolder).Count());
            }
        }

        [Fact]
        public async Task GetDownloadResultUtility_WithTwoOfTheSameDownloads_DoesNotCollide()
        {
            // Arrange
            var uri = new Uri("http://fake/content.nupkg");
            var expectedContent = "TestContent";
            var identity = new PackageIdentity("PackageA", NuGetVersion.Parse("1.0.0-Beta"));
            var testHttpSource = new TestHttpSource(
                new PackageSource("http://fake"),
                new Dictionary<string, string>
                {
                    { uri.ToString(), expectedContent }
                });
            var logger = new TestLogger();
            var token = CancellationToken.None;

            using (var cacheContext = new SourceCacheContext())
            using (var downloadDirectory = TestFileSystemUtility.CreateRandomTestFolder())
            using (var globalPackagesFolder = TestFileSystemUtility.CreateRandomTestFolder())
            {
                var downloadContext = new PackageDownloadContext(
                    cacheContext,
                    downloadDirectory);

                // Act
                using (var resultA = await GetDownloadResultUtility.GetDownloadResultAsync(
                    testHttpSource,
                    identity,
                    uri,
                    downloadContext,
                    globalPackagesFolder,
                    logger,
                    token))
                using (var resultB = await GetDownloadResultUtility.GetDownloadResultAsync(
                    testHttpSource,
                    identity,
                    uri,
                    downloadContext,
                    globalPackagesFolder,
                    logger,
                    token))
                {
                    // Assert
                    var files = Directory.EnumerateFileSystemEntries(downloadDirectory).ToArray();
                    Assert.Equal(2, files.Length);
                    Assert.StartsWith("packagea.1.0.0-beta.", Path.GetFileName(files[0]));
                    Assert.StartsWith("packagea.1.0.0-beta.", Path.GetFileName(files[1]));

                    var actualContentA = new StreamReader(resultA.PackageStream).ReadToEnd();
                    Assert.Equal(expectedContent, actualContentA);

                    var actualContentB = new StreamReader(resultB.PackageStream).ReadToEnd();
                    Assert.Equal(expectedContent, actualContentB);
                }

                Assert.Equal(0, Directory.EnumerateFileSystemEntries(downloadDirectory).Count());
                Assert.Equal(0, Directory.EnumerateFileSystemEntries(globalPackagesFolder).Count());
            }
        }
    }
}
