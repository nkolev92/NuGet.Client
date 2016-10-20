﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using NuGet.Common;
using NuGet.VisualStudio.Facade.Telemetry;

namespace NuGet.PackageManagement.UI
{
    /// <summary>
    /// Telemetry service class for restore operation
    /// </summary>
    public class RestoreTelemetryService : ActionsTelemetryBase
    {
        public static RestoreTelemetryService Instance = 
            new RestoreTelemetryService(TelemetrySession.Instance);

        public RestoreTelemetryService(ITelemetrySession telemetryService) :
            base(telemetryService)
        {

        }

        public void EmitRestoreEvent(RestoreTelemetryEvent restoreTelemetryData)
        {
            if (restoreTelemetryData == null)
            {
                throw new ArgumentNullException(nameof(restoreTelemetryData));
            }

            var detailedEvents = TelemetryServiceHelper.Instance.GetTelemetryEvents();

            if (detailedEvents != null)
            {
                // emit granular level events for current operation
                foreach (var eventName in detailedEvents.Keys)
                {
                    EmitActionStepsEvent(restoreTelemetryData.OperationId, eventName, detailedEvents[eventName]);
                }
            }

            var telemetryEvent = new TelemetryEvent(
                TelemetryConstants.RestoreActionEventName,
                new Dictionary<string, object>
                {
                    { TelemetryConstants.OperationIdPropertyName, restoreTelemetryData.OperationId },
                    { TelemetryConstants.ProjectIdsPropertyName, string.Join(",", restoreTelemetryData.ProjectIds) },
                    { TelemetryConstants.OperationSourcePropertyName, restoreTelemetryData.Source },
                    { TelemetryConstants.PackagesCountPropertyName, restoreTelemetryData.PackagesCount },
                    { TelemetryConstants.OperationStatusPropertyName, restoreTelemetryData.Status },
                    { TelemetryConstants.StartTimePropertyName, restoreTelemetryData.StartTime.ToString() },
                    { TelemetryConstants.EndTimePropertyName, restoreTelemetryData.EndTime.ToString() },
                    { TelemetryConstants.DurationPropertyName, restoreTelemetryData.Duration }
                }
            );

            _telemetrySession.PostEvent(telemetryEvent);
        }

    }
}
