using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NinjaBotCore.Services
{
    internal sealed record RoleAuditRoleChange(ulong RoleId, bool Added, bool Removed);

    internal sealed record RoleAuditCandidate(
        ulong TargetUserId,
        ulong? ExecutorUserId,
        string IntegrationType,
        string Reason,
        DateTimeOffset CreatedAt,
        IReadOnlyCollection<RoleAuditRoleChange> Changes);

    internal sealed record ModerationRoleAttribution(
        ulong? ExecutorUserId,
        string IntegrationType,
        string Reason)
    {
        public static ModerationRoleAttribution Unknown { get; } = new(null, null, null);
    }

    internal static class ModerationRoleAuditMatcher
    {
        private static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(5);

        internal static ModerationRoleAttribution Resolve(
            IEnumerable<RoleAuditCandidate> candidates,
            ulong targetUserId,
            ulong roleId,
            bool added,
            DateTimeOffset observedAt)
        {
            var matches = candidates?
                .Where(candidate => candidate.TargetUserId == targetUserId)
                .Where(candidate =>
                    Math.Abs((candidate.CreatedAt - observedAt).TotalMilliseconds)
                    <= MatchWindow.TotalMilliseconds)
                .Where(candidate => candidate.Changes?.Any(change =>
                    change.RoleId == roleId
                    && (added ? change.Added : change.Removed)) == true)
                .OrderBy(candidate => Math.Abs((candidate.CreatedAt - observedAt).TotalMilliseconds))
                .ThenByDescending(candidate => candidate.CreatedAt)
                .Take(2)
                .ToArray();

            if (matches?.Length != 1)
            {
                return ModerationRoleAttribution.Unknown;
            }

            var match = matches[0];
            return new ModerationRoleAttribution(
                match.ExecutorUserId,
                match.IntegrationType,
                match.Reason);
        }

        internal static string FormatReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return null;
            }

            var formatted = reason
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ")
                .Replace("@", "＠")
                .Trim();

            return formatted.Length <= 256
                ? formatted
                : formatted.Substring(0, 255) + "…";
        }

        internal static string BuildDescription(
            string targetMention,
            string targetUsername,
            string roleMention,
            ModerationRoleAttribution attribution)
        {
            var description = new StringBuilder();
            description.AppendLine($"{targetMention} ({targetUsername})");
            description.AppendLine($"Role: {roleMention}");
            description.AppendLine(FormatActor(attribution));

            var reason = FormatReason(attribution?.Reason);
            if (!string.IsNullOrWhiteSpace(reason))
            {
                description.AppendLine($"Reason: {reason}");
            }

            return description.ToString();
        }

        internal static string FormatActor(ModerationRoleAttribution attribution)
        {
            if (attribution?.ExecutorUserId is ulong executorUserId)
            {
                return $"Changed by: <@{executorUserId}> (ID: {executorUserId})";
            }

            if (!string.IsNullOrWhiteSpace(attribution?.IntegrationType))
            {
                var integrationType = attribution.IntegrationType
                    .Replace("\r", " ")
                    .Replace("\n", " ")
                    .Trim();
                if (integrationType.Length > 64)
                {
                    integrationType = integrationType.Substring(0, 64);
                }

                return $"Changed by: Discord integration ({integrationType})";
            }

            return "Changed by: Unknown — audit attribution unavailable";
        }
    }
}
