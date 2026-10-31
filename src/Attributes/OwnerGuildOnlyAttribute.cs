using System;
using Discord.Interactions;

namespace NinjaBotCore.Attributes
{
    /// <summary>
    /// Bot-owner tools. The module is left out of the global command list and registered only
    /// to the owner's server (config "ownerGuild"), so it doesn't use one of Discord's 100
    /// global slots and other servers never see it. Commands still need [RequireOwner].
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class OwnerGuildOnlyAttribute : DontAutoRegisterAttribute
    {
    }
}
