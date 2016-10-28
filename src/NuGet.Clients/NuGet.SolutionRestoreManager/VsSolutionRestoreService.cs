﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using NuGet.Commands;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.PackageManagement.UI;
using NuGet.PackageManagement.VisualStudio;
using NuGet.Packaging;
using NuGet.ProjectModel;
using NuGet.RuntimeModel;
using NuGet.Versioning;

namespace NuGet.SolutionRestoreManager
{
    /// <summary>
    /// Implementation of the <see cref="IVsSolutionRestoreService"/>.
    /// Provides extension API for project restore nomination triggered by 3rd party component.
    /// Configured as a single-instance MEF part.
    /// </summary>
    [PartCreationPolicy(CreationPolicy.Shared)]
    [Export(typeof(IVsSolutionRestoreService))]
    public sealed class VsSolutionRestoreService : IVsSolutionRestoreService
    {
        private const string IncludeAssets = "IncludeAssets";
        private const string ExcludeAssets = "ExcludeAssets";
        private const string PrivateAssets = "PrivateAssets";
        private const string PackageTargetFallback = "PackageTargetFallback";
        private const string RuntimeIdentifier = "RuntimeIdentifier";
        private const string RuntimeIdentifiers = "RuntimeIdentifiers";
        private const string RuntimeSupports = "RuntimeSupports";

        private readonly EnvDTE.DTE _dte;
        private readonly IProjectSystemCache _projectSystemCache;
        private readonly ISolutionRestoreWorker _restoreWorker;
        private readonly NuGet.Common.ILogger _logger;

        [ImportingConstructor]
        public VsSolutionRestoreService(
            [Import(typeof(SVsServiceProvider))]
            IServiceProvider serviceProvider,
            IProjectSystemCache projectSystemCache,
            ISolutionRestoreWorker restoreWorker,
            [Import(typeof(VisualStudioActivityLogger))]
            NuGet.Common.ILogger logger)
        {
            if (serviceProvider == null)
            {
                throw new ArgumentNullException(nameof(serviceProvider));
            }

            if (projectSystemCache == null)
            {
                throw new ArgumentNullException(nameof(projectSystemCache));
            }

            if (restoreWorker == null)
            {
                throw new ArgumentNullException(nameof(restoreWorker));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            _dte = serviceProvider.GetDTE();
            _projectSystemCache = projectSystemCache;
            _restoreWorker = restoreWorker;
            _logger = logger;
        }

        public Task<bool> CurrentRestoreOperation => _restoreWorker.CurrentRestoreOperation;

        public async Task<bool> NominateProjectAsync(string projectUniqueName, IVsProjectRestoreInfo projectRestoreInfo, CancellationToken token)
        {
            if (string.IsNullOrEmpty(projectUniqueName))
            {
                throw new ArgumentException(ProjectManagement.Strings.Argument_Cannot_Be_Null_Or_Empty, nameof(projectUniqueName));
            }

            if (projectRestoreInfo == null)
            {
                throw new ArgumentNullException(nameof(projectRestoreInfo));
            }

            if (projectRestoreInfo.TargetFrameworks == null)
            {
                throw new InvalidOperationException("TargetFrameworks cannot be null.");
            }

            try
            {
                _logger.LogInformation(
                    $"The nominate API is called for '{projectUniqueName}'.");

                var projectNames = ProjectNames.FromFullProjectPath(projectUniqueName);

                var packageSpec = ToPackageSpec(projectNames, projectRestoreInfo);
#if DEBUG
                DumpProjectRestoreInfo(packageSpec);
#endif
                _projectSystemCache.AddProjectRestoreInfo(projectNames, packageSpec);

                // returned task completes when scheduled restore operation completes.
                // it should be discarded as we don't want to block CPS on that.
                var ignored = _restoreWorker.ScheduleRestoreAsync(
                    SolutionRestoreRequest.OnUpdate(),
                    token);

                return await System.Threading.Tasks.Task.FromResult(true);
            }
            catch (Exception e)
            {
                _logger.LogError(e.ToString());
                throw;
            }
        }

        private void DumpProjectRestoreInfo(PackageSpec packageSpec)
        {
            try
            {
                var outputPath = packageSpec.RestoreMetadata.OutputPath;
                if (!Directory.Exists(outputPath))
                {
                    Directory.CreateDirectory(outputPath);
                }

                // Create dg file
                var dgFile = new DependencyGraphSpec();
                dgFile.AddRestore(packageSpec.RestoreMetadata.ProjectName);
                dgFile.AddProject(packageSpec);

                var dgPath = Path.Combine(outputPath, $"{Guid.NewGuid()}.dg");
                dgFile.Save(dgPath);
            }
            catch (Exception e)
            {
                _logger.LogError(e.ToString());
            }
        }

        private static PackageSpec ToPackageSpec(ProjectNames projectNames, IVsProjectRestoreInfo projectRestoreInfo)
        {
            var tfis = projectRestoreInfo
                .TargetFrameworks
                .Cast<IVsTargetFrameworkInfo>()
                .Select(ToTargetFrameworkInformation)
                .ToArray();

            var projectFullPath = Path.GetFullPath(projectNames.FullName);
            var projectDirectory = Path.GetDirectoryName(projectFullPath);

            // TODO: Remove temporary integration code NuGet/Home#3810
            // Initialize OTF and CT values when original value of OTF property is not provided.
            var originalTargetFrameworks = tfis
                .Select(tfi => tfi.FrameworkName.GetShortFolderName())
                .ToList();
            var crossTargeting = originalTargetFrameworks.Count > 1;

            // if "TargetFrameworks" property presents in the project file prefer the raw value.
            if (!string.IsNullOrWhiteSpace(projectRestoreInfo.OriginalTargetFrameworks))
            {
                originalTargetFrameworks = projectRestoreInfo
                    .OriginalTargetFrameworks
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
                // cross-targeting is always ON even in case of a single tfm in the list.
                crossTargeting = true;
            }

            var packageSpec = new PackageSpec(tfis)
            {
                Name = projectNames.ShortName,
                FilePath = projectFullPath,
                RestoreMetadata = new ProjectRestoreMetadata
                {
                    ProjectName = projectNames.ShortName,
                    ProjectUniqueName = projectFullPath,
                    ProjectPath = projectFullPath,
                    OutputPath = Path.GetFullPath(
                        Path.Combine(
                            projectDirectory,
                            projectRestoreInfo.BaseIntermediatePath)),
                    OutputType = RestoreOutputType.NETCore,
                    TargetFrameworks = projectRestoreInfo.TargetFrameworks
                        .Cast<IVsTargetFrameworkInfo>()
                        .Select(item => ToProjectRestoreMetadataFrameworkInfo(item, projectDirectory))
                        .ToList(),
                    OriginalTargetFrameworks = originalTargetFrameworks,
                    CrossTargeting = crossTargeting
                },
                RuntimeGraph = GetRuntimeGraph(projectRestoreInfo)
            };

            return packageSpec;
        }

        private static RuntimeGraph GetRuntimeGraph(IVsProjectRestoreInfo projectRestoreInfo)
        {
            var runtimes = projectRestoreInfo
                .TargetFrameworks
                .Cast<IVsTargetFrameworkInfo>()
                .SelectMany(tfi => new[]
                {
                    GetPropertyValueOrDefault(tfi.Properties, RuntimeIdentifier),
                    GetPropertyValueOrDefault(tfi.Properties, RuntimeIdentifiers),
                })
                .SelectMany(s => s.Split(';'))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .Select(rid => new RuntimeDescription(rid))
                .ToList();

            var supports = projectRestoreInfo
                .TargetFrameworks
                .Cast<IVsTargetFrameworkInfo>()
                .Select(tfi => GetPropertyValueOrDefault(tfi.Properties, RuntimeSupports))
                .SelectMany(s => s.Split(';'))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .Select(s => new CompatibilityProfile(s))
                .ToList();

            return new RuntimeGraph(runtimes, supports);
        }

        private static TargetFrameworkInformation ToTargetFrameworkInformation(
            IVsTargetFrameworkInfo targetFrameworkInfo)
        {
            var tfi = new TargetFrameworkInformation
            {
                FrameworkName = NuGetFramework.Parse(targetFrameworkInfo.TargetFrameworkMoniker)
            };

            var ptf = GetPropertyValueOrDefault(targetFrameworkInfo.Properties, PackageTargetFallback);
            if (!string.IsNullOrEmpty(ptf))
            {
                var fallbackList = ptf
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(NuGetFramework.Parse)
                    .ToList();

                tfi.Imports = fallbackList;

                // Update the PackageSpec framework to include fallback frameworks
                if (tfi.Imports.Count != 0)
                {
                    tfi.FrameworkName = new FallbackFramework(tfi.FrameworkName, fallbackList);
                }
            }

            if (targetFrameworkInfo.PackageReferences != null)
            {
                tfi.Dependencies.AddRange(
                    targetFrameworkInfo.PackageReferences
                        .Cast<IVsReferenceItem>()
                        .Select(ToPackageLibraryDependency));
            }

            return tfi;
        }

        private static ProjectRestoreMetadataFrameworkInfo ToProjectRestoreMetadataFrameworkInfo(
            IVsTargetFrameworkInfo targetFrameworkInfo,
            string projectDirectory)
        {
            var tfi = new ProjectRestoreMetadataFrameworkInfo
            {
                FrameworkName = NuGetFramework.Parse(targetFrameworkInfo.TargetFrameworkMoniker)
            };

            if (targetFrameworkInfo.ProjectReferences != null)
            {
                tfi.ProjectReferences.AddRange(
                    targetFrameworkInfo.ProjectReferences
                        .Cast<IVsReferenceItem>()
                        .Select(item => ToProjectRestoreReference(item, projectDirectory)));
            }

            return tfi;
        }

        private static LibraryDependency ToPackageLibraryDependency(IVsReferenceItem item)
        {
            var dependency = new LibraryDependency
            {
                LibraryRange = new LibraryRange(
                    name: item.Name,
                    versionRange: GetVersionRange(item),
                    typeConstraint: LibraryDependencyTarget.Package)
            };

            MSBuildRestoreUtility.ApplyIncludeFlags(
                dependency,
                includeAssets: GetPropertyValueOrDefault(item, IncludeAssets),
                excludeAssets: GetPropertyValueOrDefault(item, ExcludeAssets),
                privateAssets: GetPropertyValueOrDefault(item, PrivateAssets));

            return dependency;
        }

        private static ProjectRestoreReference ToProjectRestoreReference(IVsReferenceItem item, string projectDirectory)
        {
            // The path may be a relative path, to match the project unique name as a
            // string this should be the full path to the project
            // Remove ../../ and any other relative parts of the path that were used in the project file
            var referencePath = Path.GetFullPath(Path.Combine(projectDirectory, item.Name));

            var dependency = new ProjectRestoreReference
            {
                ProjectPath = referencePath,
                ProjectUniqueName = referencePath,
            };

            MSBuildRestoreUtility.ApplyIncludeFlags(
                dependency,
                includeAssets: GetPropertyValueOrDefault(item, IncludeAssets),
                excludeAssets: GetPropertyValueOrDefault(item, ExcludeAssets),
                privateAssets: GetPropertyValueOrDefault(item, PrivateAssets));

            return dependency;
        }

        private static VersionRange GetVersionRange(IVsReferenceItem item)
        {
            var versionRange = GetPropertyValueOrDefault(item, "Version");

            if (!string.IsNullOrEmpty(versionRange))
            {
                return VersionRange.Parse(versionRange);
            }

            return VersionRange.All;
        }

        private static string GetPropertyValueOrDefault(
            IVsReferenceItem item, string propertyName, string defaultValue = "")
        {
            try
            {
                return item.Properties?.Item(propertyName)?.Value ?? defaultValue;
            }
            catch (ArgumentException)
            {
            }
            catch (KeyNotFoundException)
            {
            }

            return defaultValue;
        }

        private static string GetPropertyValueOrDefault(
            IVsProjectProperties properties, string propertyName, string defaultValue = "")
        {
            try
            {
                return properties?.Item(propertyName)?.Value ?? defaultValue;
            }
            catch (ArgumentException)
            {
            }
            catch (KeyNotFoundException)
            {
            }

            return defaultValue;
        }
    }
}
