﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.ProjectModel;

namespace NuGet.Commands
{
    /// <summary>
    /// Shared code to run the "restore" command for dotnet restore, nuget.exe, and VS.
    /// </summary>
    public static class RestoreRunner
    {
        /// <summary>
        /// Create requests, execute requests, and commit restore results.
        /// </summary>
        public static async Task<IReadOnlyList<RestoreSummary>> Run(RestoreArgs restoreContext)
        {
            // Create requests
            var requests = await GetRequests(restoreContext);

            // Run requests
            return await Run(requests, restoreContext);
        }

        /// <summary>
        /// Execute and commit restore requests.
        /// </summary>
        public static async Task<IReadOnlyList<RestoreSummary>> Run(
            IEnumerable<RestoreSummaryRequest> restoreRequests,
            RestoreArgs restoreContext)
        {
            int maxTasks = GetMaxTaskCount(restoreContext);

            var log = restoreContext.Log;

            if (maxTasks > 1)
            {
                log.LogVerbose(string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Log_RunningParallelRestore,
                    maxTasks));
            }
            else
            {
                log.LogVerbose(Strings.Log_RunningNonParallelRestore);
            }

            // Get requests
            var requests = new Queue<RestoreSummaryRequest>(restoreRequests);
            var restoreTasks = new List<Task<RestoreSummary>>(maxTasks);
            var restoreSummaries = new List<RestoreSummary>(requests.Count);

            // Run requests
            while (requests.Count > 0)
            {
                // Throttle and wait for a task to finish if we have hit the limit
                if (restoreTasks.Count == maxTasks)
                {
                    var restoreSummary = await CompleteTaskAsync(restoreTasks);
                    restoreSummaries.Add(restoreSummary);
                }

                var request = requests.Dequeue();

                var task = Task.Run(async () => await ExecuteAndCommit(request));
                restoreTasks.Add(task);
            }

            // Wait for all restores to finish
            while (restoreTasks.Count > 0)
            {
                var restoreSummary = await CompleteTaskAsync(restoreTasks);
                restoreSummaries.Add(restoreSummary);
            }

            // Summary
            return restoreSummaries;
        }

        /// <summary>
        /// Execute and commit restore requests.
        /// </summary>
        public static async Task<IReadOnlyList<RestoreResultPair>> RunWithoutCommit(
            IEnumerable<RestoreSummaryRequest> restoreRequests,
            RestoreArgs restoreContext)
        {
            int maxTasks = GetMaxTaskCount(restoreContext);

            var log = restoreContext.Log;

            if (maxTasks > 1)
            {
                log.LogVerbose(string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Log_RunningParallelRestore,
                    maxTasks));
            }
            else
            {
                log.LogVerbose(Strings.Log_RunningNonParallelRestore);
            }

            // Get requests
            var requests = new Queue<RestoreSummaryRequest>(restoreRequests);
            var restoreTasks = new List<Task<RestoreResultPair>>(maxTasks);
            var restoreResults = new List<RestoreResultPair>(maxTasks);

            // Run requests
            while (requests.Count > 0)
            {
                // Throttle and wait for a task to finish if we have hit the limit
                if (restoreTasks.Count == maxTasks)
                {
                    var restoreSummary = await CompleteTaskAsync(restoreTasks);
                    restoreResults.Add(restoreSummary);
                }

                var request = requests.Dequeue();

                var task = Task.Run(async () => await Execute(request));
                restoreTasks.Add(task);
            }

            // Wait for all restores to finish
            while (restoreTasks.Count > 0)
            {
                var restoreSummary = await CompleteTaskAsync(restoreTasks);
                restoreResults.Add(restoreSummary);
            }

            // Summary
            return restoreResults;
        }

        /// <summary>
        /// Create restore requests but do not execute them.
        /// </summary>
        public static async Task<IReadOnlyList<RestoreSummaryRequest>> GetRequests(RestoreArgs restoreContext)
        {
            // Get requests
            var requests = new List<RestoreSummaryRequest>();

            var inputs = new List<string>(restoreContext.Inputs);

            // If there are no inputs, use the current directory
            if (restoreContext.PreLoadedRequestProviders.Count < 1 && !inputs.Any())
            {
                inputs.Add(Path.GetFullPath("."));
            }

            // Ignore casing on windows and mac
            var comparer = (RuntimeEnvironmentHelper.IsWindows || RuntimeEnvironmentHelper.IsMacOSX) ?
                StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

            var uniqueRequest = new HashSet<string>(comparer);

            // Create requests
            // Pre-loaded requests
            foreach (var request in await CreatePreLoadedRequests(restoreContext))
            {
                // De-dupe requests
                if (request.Request.LockFilePath == null
                    || uniqueRequest.Add(request.Request.LockFilePath))
                {
                    requests.Add(request);
                }
            }

            // Input based requests
            foreach (var input in inputs)
            {
                var inputRequests = await CreateRequests(input, restoreContext);
                if (inputRequests.Count == 0)
                {
                    // No need to throw here - the situation is harmless, and we want to report all possible
                    // inputs that don't resolve to a project.
                    restoreContext.Log.LogWarning(string.Format(
                            CultureInfo.CurrentCulture,
                            Strings.Error_UnableToLocateRestoreTarget,
                            Path.GetFullPath(input)));
                }
                foreach (var request in inputRequests)
                {
                    // De-dupe requests
                    if (uniqueRequest.Add(request.Request.LockFilePath))
                    {
                        requests.Add(request);
                    }
                }
            }

            return requests;
        }

        private static int GetMaxTaskCount(RestoreArgs restoreContext)
        {
            var maxTasks = 1;

            if (!restoreContext.DisableParallel && !RuntimeEnvironmentHelper.IsMono)
            {
                maxTasks = Environment.ProcessorCount;
            }

            if (maxTasks < 1)
            {
                maxTasks = 1;
            }

            return maxTasks;
        }

        private static async Task<RestoreSummary> ExecuteAndCommit(RestoreSummaryRequest summaryRequest)
        {
            var result = await Execute(summaryRequest);

            return await Commit(result);
        }

        private static async Task<RestoreResultPair> Execute(RestoreSummaryRequest summaryRequest)
        {
            var log = summaryRequest.Request.Log;

            log.LogVerbose(string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Log_ReadingProject,
                    summaryRequest.InputPath));

            // Run the restore
            var request = summaryRequest.Request;

            // Read the existing lock file, this is needed to support IsLocked=true
            // This is done on the thread and not as part of creating the request due to
            // how long it takes to load the lock file.
            if (request.ExistingLockFile == null)
            {
                request.ExistingLockFile = LockFileUtilities.GetLockFile(request.LockFilePath, log);
            }

            var command = new RestoreCommand(request);
            var result = await command.ExecuteAsync();

            return new RestoreResultPair(summaryRequest, result);
        }

        public static async Task<RestoreSummary> Commit(RestoreResultPair restoreResult)
        {
            var summaryRequest = restoreResult.SummaryRequest;
            var result = restoreResult.Result;

            var log = summaryRequest.Request.Log;

            // Commit the result
            log.LogInformation(Strings.Log_Committing);
            await result.CommitAsync(log, CancellationToken.None);

            if (result.Success)
            {
                log.LogMinimal(string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Log_RestoreComplete,
                    DatetimeUtility.ToReadableTimeFormat(result.ElapsedTime),
                    summaryRequest.InputPath));
            }
            else
            {
                log.LogMinimal(string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Log_RestoreFailed,
                    DatetimeUtility.ToReadableTimeFormat(result.ElapsedTime),
                    summaryRequest.InputPath));
            }

            // Build the summary
            return new RestoreSummary(
                result,
                summaryRequest.InputPath,
                summaryRequest.Settings,
                summaryRequest.Sources,
                summaryRequest.CollectorLogger.Errors);
        }

        private static async Task<RestoreSummary> CompleteTaskAsync(List<Task<RestoreSummary>> restoreTasks)
        {
            var doneTask = await Task.WhenAny(restoreTasks);
            restoreTasks.Remove(doneTask);
            return await doneTask;
        }

        private static async Task<RestoreResultPair>
            CompleteTaskAsync(List<Task<RestoreResultPair>> restoreTasks)
        {
            var doneTask = await Task.WhenAny(restoreTasks);
            restoreTasks.Remove(doneTask);
            return await doneTask;
        }

        private static async Task<IReadOnlyList<RestoreSummaryRequest>> CreatePreLoadedRequests(
            RestoreArgs restoreContext)
        {
            var results = new List<RestoreSummaryRequest>();

            foreach (var provider in restoreContext.PreLoadedRequestProviders)
            {
                var requests = await provider.CreateRequests(restoreContext);
                results.AddRange(requests);
            }

            return results;
        }

        private static async Task<IReadOnlyList<RestoreSummaryRequest>> CreateRequests(
            string input,
            RestoreArgs restoreContext)
        {
            foreach (var provider in restoreContext.RequestProviders)
            {
                if (await provider.Supports(input))
                {
                    return await provider.CreateRequests(
                        input,
                        restoreContext);
                }
            }

            if (File.Exists(input) || Directory.Exists(input))
            {
                // Not a file or directory we know about. Try to be helpful without response.
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, GetInvalidInputErrorMessage(input), input));
            }

            throw new FileNotFoundException(input);
        }

        public static string GetInvalidInputErrorMessage(string input)
        {
            Debug.Assert(File.Exists(input) || Directory.Exists(input));
            if (File.Exists(input))
            {
                var fileExtension = Path.GetExtension(input);
                if (".json".Equals(fileExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return Strings.Error_InvalidCommandLineInputJson;
                }

                if (".config".Equals(fileExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return Strings.Error_InvalidCommandLineInputConfig;
                }
            }

            return Strings.Error_InvalidCommandLineInput;
        }
    }
}
