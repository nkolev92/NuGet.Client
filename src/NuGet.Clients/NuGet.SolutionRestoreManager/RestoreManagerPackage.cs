﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using NuGet.Configuration;
using NuGet.PackageManagement;
using NuGet.PackageManagement.VisualStudio;
using NuGet.Protocol.Core.Types;
using Task = System.Threading.Tasks.Task;

namespace NuGet.SolutionRestoreManager
{
    /// <summary>
    /// Visual Studio extension package designed to bootstrap solution restore components.
    /// Loads on solution open to attach to build events.
    /// </summary>
    // Flag AllowsBackgroundLoading is set to False because switching to Main thread wiht JTF is creating
    // performance overhead in InitializeAsync() API.
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = false)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string)]
    [Guid(PackageGuidString)]
    public sealed class RestoreManagerPackage : AsyncPackage
    {
        public const string ProductVersion = "4.0.0";

        /// <summary>
        /// RestoreManagerPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "2b52ac92-4551-426d-bd34-c6d7d9fdd1c5";

        [Import]
        private Lazy<ISolutionRestoreWorker> SolutionRestoreWorker { get; set; }

        [Import]
        private Lazy<ISettings> Settings { get; set; }

        [Import]
        private Lazy<IVsSolutionManager> SolutionManager { get; set; }

        // keeps a reference to BuildEvents so that our event handler
        // won't get disconnected.
        private EnvDTE.BuildEvents _buildEvents;

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            componentModel.DefaultCompositionService.SatisfyImportsOnce(this);

            // Accessing DTE without confirming to Main thread because AllowsBackgroundLoading is false which
            // will make sure that this piece is always executed on Main thread.
            var dte = (EnvDTE.DTE)await GetServiceAsync(typeof(SDTE));
            _buildEvents = dte.Events.BuildEvents;
            _buildEvents.OnBuildBegin += BuildEvents_OnBuildBegin;

            UserAgent.SetUserAgentString(
                new UserAgentStringBuilder().WithVisualStudioSKU(dte.GetFullVsVersionString()));

            await SolutionRestoreCommand.InitializeAsync(this);

            await base.InitializeAsync(cancellationToken, progress);
        }

        private void BuildEvents_OnBuildBegin(
            EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction Action)
        {
            if (Action == EnvDTE.vsBuildAction.vsBuildActionClean)
            {
                // Clear the project.json restore cache on clean to ensure that the next build restores again
                SolutionRestoreWorker.Value.CleanCache();

                return;
            }

            // Check if solution is DPL enabled, then don't restore
            if (SolutionManager.Value.IsSolutionDPLEnabled)
            {
                return;
            }

            if (!ShouldRestoreOnBuild)
            {
                return;
            }

            var forceRestore = Action == EnvDTE.vsBuildAction.vsBuildActionRebuildAll;

            // Execute
            SolutionRestoreWorker.Value.Restore(SolutionRestoreRequest.OnBuild(forceRestore));
        }

        /// <summary>
        /// Returns true if automatic package restore on build is enabled.
        /// </summary>
        private bool ShouldRestoreOnBuild
        {
            get
            {
                var packageRestoreConsent = new PackageRestoreConsent(Settings.Value);
                return packageRestoreConsent.IsAutomatic;
            }
        }
    }
}
