using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using NuGet.Commands;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.ProjectModel;
using NuGet.Versioning;
using NuGet.Frameworks;
using NuGet.Packaging.Core;

namespace NuGet.Build.Tasks.Pack
{
    public class PackTask : Microsoft.Build.Utilities.Task
    {
        //TODO: Add support for Tools
        [Required]
        public ITaskItem PackItem { get; set; }
        public ITaskItem[] PackageFiles { get; set; }
        public ITaskItem[] TargetFrameworks { get; set; }
        public string[] PackageTypes { get; set; }
        public string PackageId { get; set; }
        public string PackageVersion { get; set; }
        public string[] Authors { get; set; }
        public string Description { get; set; }
        public string Copyright { get; set; }
        public bool RequireLicenseAcceptance { get; set; }
        public string LicenseUrl { get; set; }
        public string ProjectUrl { get; set; }
        public string IconUrl { get; set; }
        public string[] Tags { get; set; }
        public string ReleaseNotes { get; set; }
        public string Configuration { get; set; }
        public string[] TargetPath { get; set; }
        public string AssemblyName { get; set; }
        public string PackageOutputPath { get; set; }
        public bool IsTool { get; set; }
        public bool IncludeSymbols { get; set; }
        public bool IncludeSource { get; set; }
        public string RepositoryUrl { get; set; }
        public string RepositoryType { get; set; }
        public ITaskItem[] ProjectReferences { get; set; }
        public ITaskItem[] SourceFiles { get; set; }
        public bool NoPackageAnalysis { get; set; }
        public string MinClientVersion { get; set; }
        public bool Serviceable { get; set; }
        public string VersionSuffix { get; set; }


        public override bool Execute()
        {
#if DEBUG
            System.Diagnostics.Debugger.Launch();
#endif
            var packArgs = GetPackArgs();
            var packageBuilder = GetPackageBuilder(packArgs);
            var contentFiles = ProcessContentToIncludeInPackage();
            packArgs.PackTargetArgs.ContentFiles = contentFiles;
            ProcessJsonFile(packageBuilder, packArgs.CurrentDirectory,
                Path.GetFileName(packArgs.Path), isHostProject:true);
            PackCommandRunner packRunner = new PackCommandRunner(packArgs, MSBuildProjectFactory.ProjectCreator, packageBuilder);
            packRunner.BuildPackage();
            return true;
        }

        private PackArgs GetPackArgs()
        {
            var packArgs = new PackArgs();
            packArgs.Logger = new MSBuildLogger(Log);
            packArgs.OutputDirectory = PackageOutputPath;
            packArgs.Serviceable = Serviceable;
            packArgs.Suffix = VersionSuffix;
            packArgs.Tool = IsTool;
            packArgs.Symbols = IncludeSymbols;
            packArgs.NoPackageAnalysis = NoPackageAnalysis;
            packArgs.PackTargetArgs = new MSBuildPackTargetArgs()
            {
                TargetPath = TargetPath,
                Configuration = Configuration,
                AssemblyName = AssemblyName
            };
            packArgs.PackTargetArgs.TargetFrameworks = ParseFrameworks();
            if (MinClientVersion != null)
            {
                Version version;
                if (!Version.TryParse(MinClientVersion, out version))
                {
                    throw new ArgumentException("MinClientVersion is invalid");
                }
                packArgs.MinClientVersion = version;
            }

            InitCurrentDirectoryAndFileName(packArgs);

            if (IncludeSource)
            {
                packArgs.PackTargetArgs.SourceFiles = GetSourceFiles(packArgs.CurrentDirectory);
                packArgs.Symbols = IncludeSource;
            }

            PackCommandRunner.SetupCurrentDirectory(packArgs);

            return packArgs;
        }

        private ISet<NuGetFramework> ParseFrameworks()
        {
            var nugetFrameworks = new HashSet<NuGetFramework>();
            if (TargetFrameworks != null)
            {
                nugetFrameworks = new HashSet<NuGetFramework>(TargetFrameworks.Select(t => NuGetFramework.Parse(t.ItemSpec)));
            }
            return nugetFrameworks;
        } 

        private PackageBuilder GetPackageBuilder(PackArgs packArgs)
        {
            PackageBuilder builder = new PackageBuilder();
            builder.Id = PackageId;
            if (PackageVersion != null)
            {
                NuGetVersion version;
                if (!NuGetVersion.TryParse(PackageVersion, out version))
                {
                    throw new ArgumentException(String.Format(CultureInfo.CurrentCulture, "PackageVersion string specified '{0}' is invalid.", PackageVersion));
                }
                builder.Version = version;
            }
            else
            {
                builder.Version = new NuGetVersion("1.0.0");
            }

            if (Authors != null)
            {
                builder.Authors.AddRange(Authors);
            }

            if (Tags != null)
            {
                builder.Tags.AddRange(Tags);
            }

            builder.Description = Description;
            builder.Copyright = Copyright;
            builder.ReleaseNotes = ReleaseNotes;
            Uri tempUri;
            if (Uri.TryCreate(LicenseUrl, UriKind.Absolute, out tempUri))
            {
                builder.LicenseUrl = tempUri;
            }
            if (Uri.TryCreate(ProjectUrl, UriKind.Absolute, out tempUri))
            {
                builder.ProjectUrl = tempUri;
            }
            if (Uri.TryCreate(IconUrl, UriKind.Absolute, out tempUri))
            {
                builder.IconUrl = tempUri;
            }
            builder.RequireLicenseAcceptance = RequireLicenseAcceptance;
            if (!string.IsNullOrEmpty(RepositoryUrl) || !string.IsNullOrEmpty(RepositoryType))
            {
                builder.Repository = new RepositoryMetadata(RepositoryType, RepositoryUrl);
            }
            builder.PackageTypes = ParsePackageTypes();
            ParseProjectToProjectReferences(builder, packArgs);
            return builder;
        }

        private ICollection<PackageType> ParsePackageTypes()
        {
            var listOfPackageTypes = new List<PackageType>();
            if (PackageTypes != null)
            {
                foreach (var packageType in PackageTypes)
                {
                    string[] packageTypeSplitInPart = packageType.Split(new char[] {','});
                    string packageTypeName = packageTypeSplitInPart[0];
                    var version = PackageType.EmptyVersion;
                    if (packageTypeSplitInPart.Length > 1)
                    {
                        string versionString = packageTypeSplitInPart[1];
                        Version.TryParse(versionString, out version);
                    }
                    listOfPackageTypes.Add(new PackageType(packageTypeName, version));
                }
            }
            return listOfPackageTypes;
        }

        private void ParseProjectToProjectReferences(PackageBuilder packageBuilder, PackArgs packArgs)
        {
            var dependencies = new HashSet<PackageDependency>();
            var projectReferences = new List<ProjectToProjectReference>();
            if (ProjectReferences != null)
            {
                foreach (var p2pReference in ProjectReferences)
                {
                    var referenceString = p2pReference.ItemSpec;
                    string[] param = referenceString.Split(new char[] {'|'}, StringSplitOptions.RemoveEmptyEntries);
                    if (param[0] == "PROJECT")
                    {
                        // This is a project reference, and the DLL should be copied over to lib.
                        projectReferences.Add(new ProjectToProjectReference()
                        {
                            TargetPath = param[1],
                            AssemblyName = param[2]
                        });
                        string projectPath = param[3];
                        ProcessJsonFile(packageBuilder, Path.GetDirectoryName(projectPath), Path.GetFileName(projectPath), isHostProject:false);
                    }
                    else if (param[0] == "PACKAGE")
                    {
                        // This is to be treated as a nupkg dependency, add as library dependency.
                        var packageId = param[1];

                        //TODO: Do the work to get the version from AssemblyInfo.cs

                        var version = "1.0.0";
                        if (param.Count() == 3)
                        {
                            version = param[2];
                        }
                        var packageDependency = new PackageDependency(packageId, VersionRange.Parse(version));
                        dependencies.Add(packageDependency);
                    }
                }

                packageBuilder.DependencyGroups.Add(new PackageDependencyGroup(NuGetFramework.AnyFramework, dependencies));
                packArgs.PackTargetArgs.ProjectReferences = projectReferences;
            }
        }
        private void InitCurrentDirectoryAndFileName(PackArgs packArgs)
        {
            if (PackItem == null)
            {
                throw new InvalidOperationException(nameof(PackItem));
            }
            else
            {
                packArgs.CurrentDirectory = Path.Combine(PackItem.GetMetadata("RootDir"), PackItem.GetMetadata("Directory"));
                packArgs.Arguments = new string[] {string.Concat(PackItem.GetMetadata("FileName"), PackItem.GetMetadata("Extension"))};
                packArgs.Path = PackItem.GetMetadata("FullPath");
                packArgs.Exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void ProcessJsonFile(PackageBuilder packageBuilder, string currentDirectory, string projectFileName, bool isHostProject)
        {
            string path = ProjectJsonPathUtilities.GetProjectConfigPath(currentDirectory, projectFileName);
            if (!File.Exists(path))
            {
                return;
            }
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                var spec = JsonPackageSpecReader.GetPackageSpec(stream, packageBuilder.Id, path, VersionSuffix);
                if (spec.TargetFrameworks.Any())
                {
                    foreach (var framework in spec.TargetFrameworks)
                    {
                        if (framework.FrameworkName.IsUnsupported)
                        {
                            throw new Exception(String.Format(CultureInfo.CurrentCulture, NuGet.Commands.Strings.Error_InvalidTargetFramework, framework.FrameworkName));
                        }
                        if (isHostProject)
                        {
                            packageBuilder.TargetFrameworks.Add(framework.FrameworkName);
                        }
                        PackCommandRunner.AddDependencyGroups(framework.Dependencies.Concat(spec.Dependencies), framework.FrameworkName, packageBuilder);
                    }
                }
                else
                {
                    if (spec.Dependencies.Any())
                    {
                        PackCommandRunner.AddDependencyGroups(spec.Dependencies, NuGetFramework.AnyFramework, packageBuilder);
                    }
                }
            }
        }

        private Dictionary<string, HashSet<string>> ProcessContentToIncludeInPackage()
        {
            // This maps from source path on disk to target path inside the nupkg.
            var fileModel = new Dictionary<string, HashSet<string>>();
            if (PackageFiles != null)
            {
                var excludeFiles = CalculateFilesToExcludeInPack();
                foreach (var packageFile in PackageFiles)
                {
                    string targetPath = string.Empty;
                    var customMetadata = packageFile.CloneCustomMetadata();
                    var sourcePath = GetSourcePath(packageFile, customMetadata);
                    if (excludeFiles.Contains(sourcePath))
                    {
                        continue;
                    }
                    if (customMetadata.Contains("PackagePath"))
                    {
                        targetPath = packageFile.GetMetadata("PackagePath");
                    }

                    if (fileModel.ContainsKey(sourcePath))
                    {
                        var setOfTargetPaths = fileModel[sourcePath];
                        if (!setOfTargetPaths.Contains(targetPath))
                        {
                            setOfTargetPaths.Add(targetPath);
                        }
                    }
                    else
                    {
                        var setOfTargetPaths = new HashSet<string>();
                        setOfTargetPaths.Add(targetPath);
                        fileModel.Add(sourcePath, setOfTargetPaths);
                    }
                }
            }

            return fileModel;
        }

        private string GetSourcePath(ITaskItem packageFile, IDictionary customMetadata)
        {
            string sourcePath = packageFile.GetMetadata("FullPath");
            if (customMetadata.Contains("MSBuildSourceProjectFile"))
            {
                string sourceProjectFile = packageFile.GetMetadata("MSBuildSourceProjectFile");
                string identity = packageFile.GetMetadata("Identity");
                sourcePath = Path.Combine(sourceProjectFile.Replace(Path.GetFileName(sourceProjectFile), string.Empty), identity);
            }
            return Path.GetFullPath(sourcePath);
        }

        private ISet<string> CalculateFilesToExcludeInPack()
        {
            var excludeFiles = new HashSet<string>();
            foreach (var file in PackageFiles)
            {
                var customMetadata = file.CloneCustomMetadata();
                string sourcePath = GetSourcePath(file,  customMetadata);
                if (ExcludeInPack(file, customMetadata))
                {
                    excludeFiles.Add(sourcePath);
                }
            }
            return excludeFiles;
        }

        private bool ExcludeInPack(ITaskItem packageFile, IDictionary customMetadata)
        {
            if (customMetadata.Contains("Pack"))
            {
                string shouldPack = packageFile.GetMetadata("Pack");
                bool pack;
                Boolean.TryParse(shouldPack, out pack);
                return !pack;
            }
            return false;
        }

        private IDictionary<string,string> GetSourceFiles(string currentProjectDirectory)
        {
            var sourceFiles = new Dictionary<string, string>();
            if (SourceFiles != null)
            {
                foreach (var src in SourceFiles)
                {
                    var customMetadata = src.CloneCustomMetadata();
                    var sourcePath = GetSourcePath(src, customMetadata);
                    string sourceProjectFile = currentProjectDirectory;
                    if (customMetadata.Contains("MSBuildSourceProjectFile"))
                    {
                        sourceProjectFile = src.GetMetadata("MSBuildSourceProjectFile");
                        sourceProjectFile = Path.GetDirectoryName(sourceProjectFile);
                    }

                    sourceFiles.Add(sourcePath, sourceProjectFile);
                }
            }
            return sourceFiles;
        } 
    }
}
