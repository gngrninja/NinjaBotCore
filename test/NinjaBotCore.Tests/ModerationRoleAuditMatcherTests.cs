using System;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class ModerationRoleAuditMatcherTests
    {
        [Fact]
        public void Resolve_ExactTargetRoleAndDirection_ReturnsExecutor()
        {
            var observedAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
            var candidates = new[]
            {
                new RoleAuditCandidate(
                    42,
                    1001,
                    null,
                    "Routine access change",
                    observedAt.AddMilliseconds(-250),
                    new[] { new RoleAuditRoleChange(77, true, false) })
            };

            var result = ModerationRoleAuditMatcher.Resolve(
                candidates,
                targetUserId: 42,
                roleId: 77,
                added: true,
                observedAt);

            Assert.Equal((ulong)1001, result.ExecutorUserId);
            Assert.Equal("Routine access change", result.Reason);
            Assert.Equal("Changed by: <@1001> (ID: 1001)", ModerationRoleAuditMatcher.FormatActor(result));
        }

        [Fact]
        public void Resolve_RejectsWrongTargetRoleDirectionAndStaleEntries()
        {
            var observedAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
            var candidates = new[]
            {
                Candidate(target: 41, role: 77, added: true, at: observedAt, executor: 1),
                Candidate(target: 42, role: 78, added: true, at: observedAt, executor: 2),
                Candidate(target: 42, role: 77, added: false, at: observedAt, executor: 3),
                Candidate(target: 42, role: 77, added: true, at: observedAt.AddSeconds(-20), executor: 4)
            };

            var result = ModerationRoleAuditMatcher.Resolve(
                candidates,
                targetUserId: 42,
                roleId: 77,
                added: true,
                observedAt);

            Assert.Null(result.ExecutorUserId);
            Assert.Equal("Changed by: Unknown — audit attribution unavailable", ModerationRoleAuditMatcher.FormatActor(result));
        }

        [Fact]
        public void Resolve_MultipleExactEntries_ReturnsUnknownRatherThanGuessing()
        {
            var observedAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
            var candidates = new[]
            {
                Candidate(target: 42, role: 77, added: true, at: observedAt.AddSeconds(-4), executor: 10),
                Candidate(target: 42, role: 77, added: true, at: observedAt.AddMilliseconds(300), executor: 11)
            };

            var result = ModerationRoleAuditMatcher.Resolve(
                candidates,
                targetUserId: 42,
                roleId: 77,
                added: true,
                observedAt);

            Assert.Null(result.ExecutorUserId);
            Assert.Equal(
                "Changed by: Unknown — audit attribution unavailable",
                ModerationRoleAuditMatcher.FormatActor(result));
        }

        [Fact]
        public void FormatActor_UsesIntegrationWithoutInventingAUser()
        {
            var attribution = new ModerationRoleAttribution(
                ExecutorUserId: null,
                IntegrationType: "twitch",
                Reason: null);

            Assert.Equal(
                "Changed by: Discord integration (twitch)",
                ModerationRoleAuditMatcher.FormatActor(attribution));
        }

        [Fact]
        public void FormatReason_BoundsAndNeutralizesUnsafeText()
        {
            var reason = "first line\n@everyone " + new string('x', 400);

            var formatted = ModerationRoleAuditMatcher.FormatReason(reason);

            Assert.DoesNotContain("\n", formatted);
            Assert.DoesNotContain("@everyone", formatted);
            Assert.True(formatted.Length <= 256);
        }

        [Fact]
        public void BuildDescription_IncludesTargetRoleActorAndSafeReason()
        {
            var attribution = new ModerationRoleAttribution(
                ExecutorUserId: 1001,
                IntegrationType: null,
                Reason: "approved\n@everyone");

            var description = ModerationRoleAuditMatcher.BuildDescription(
                "<@42>",
                "Target",
                "<@&77>",
                attribution);

            Assert.Contains("<@42> (Target)", description);
            Assert.Contains("Role: <@&77>", description);
            Assert.Contains("Changed by: <@1001> (ID: 1001)", description);
            Assert.Contains("Reason: approved ＠everyone", description);
        }

        [Fact]
        public void BuildDescription_UnknownActorDoesNotInventAReason()
        {
            var description = ModerationRoleAuditMatcher.BuildDescription(
                "<@42>",
                "Target",
                "<@&77>",
                ModerationRoleAttribution.Unknown);

            Assert.Contains("Changed by: Unknown — audit attribution unavailable", description);
            Assert.DoesNotContain("Reason:", description);
        }

        private static RoleAuditCandidate Candidate(
            ulong target,
            ulong role,
            bool added,
            DateTimeOffset at,
            ulong executor) =>
            new(
                target,
                executor,
                null,
                null,
                at,
                new[] { new RoleAuditRoleChange(role, added, !added) });
    }
}
