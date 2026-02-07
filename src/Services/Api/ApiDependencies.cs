using System;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Services.Api
{
    public record ApiDependencies(
        ILogger Logger,
        IServiceProvider ServiceProvider,
        HelpContentProvider HelpProvider,
        WowUtilities WowUtilities,
        string ApiKey);
}
