// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;

namespace NuGet.Protocol
{
    public class HttpCacheResult
    {
        public HttpCacheResult(TimeSpan maxAge, string readFile, string newFile, string writeFile)
        {
            MaxAge = maxAge;
            ReadFile = readFile;
            NewFile = newFile;
            WriteFile = writeFile;
        }

        public TimeSpan MaxAge { get; }
        public string ReadFile { get; }
        public string NewFile { get; }
        public string WriteFile { get; }
        public Stream Stream { get; set; }
    }
}
