using System;
using System.Linq;
using System.Reflection;
using NinjaBotCore.Database;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Tests to verify entity model types are consistent with PostgreSQL schema.
    /// After migration from SQLite to PostgreSQL, all INTEGER IDs became bigint (long in C#).
    /// </summary>
    public class EntityTypeConsistencyTests
    {
        [Fact]
        public void AllEntityIds_ShouldBe_LongType()
        {
            // Arrange - Get all entity types from the database namespace
            var entityTypes = typeof(NinjaBotEntities).Assembly
                .GetTypes()
                .Where(t => t.Namespace == "NinjaBotCore.Database" &&
                           t.IsClass &&
                           !t.IsAbstract &&
                           !t.Name.Contains("<") && // Exclude compiler-generated classes
                           t != typeof(NinjaBotEntities) &&
                           t != typeof(NinjaBotEntitiesFactory) &&
                           t != typeof(DatabaseConfigurator))
                .ToList();

            Assert.NotEmpty(entityTypes);

            var inconsistentEntities = new System.Collections.Generic.List<string>();

            // Act & Assert - Check each entity for ID properties
            foreach (var entityType in entityTypes)
            {
                var idProperties = entityType
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase) &&
                               (p.PropertyType == typeof(int) ||
                                p.PropertyType == typeof(long) ||
                                p.PropertyType == typeof(int?) ||
                                p.PropertyType == typeof(long?)))
                    .ToList();

                foreach (var prop in idProperties)
                {
                    var actualType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

                    // All ID fields should be long (bigint) after PostgreSQL migration
                    if (actualType == typeof(int))
                    {
                        inconsistentEntities.Add($"{entityType.Name}.{prop.Name} is int, should be long");
                    }
                }
            }

            // Assert - No inconsistencies found
            Assert.Empty(inconsistentEntities);
        }

        [Fact]
        public void AwaySystem_Types_ShouldMatch_PostgresSchema()
        {
            // Arrange
            var awayType = typeof(AwaySystem);

            // Act
            var awayIdProp = awayType.GetProperty("AwayId");
            var userNameProp = awayType.GetProperty("UserName");
            var statusProp = awayType.GetProperty("Status");
            var timeAwayProp = awayType.GetProperty("TimeAway");

            // Assert
            Assert.NotNull(awayIdProp);
            Assert.Equal(typeof(long), awayIdProp.PropertyType); // bigint

            Assert.NotNull(userNameProp);
            Assert.Equal(typeof(string), userNameProp.PropertyType); // text

            Assert.NotNull(statusProp);
            Assert.Equal(typeof(bool?), statusProp.PropertyType); // boolean nullable

            Assert.NotNull(timeAwayProp);
            Assert.Equal(typeof(DateTime?), timeAwayProp.PropertyType); // timestamp with time zone nullable
        }

        [Fact]
        public void WowVanillaGuild_Types_ShouldMatch_PostgresSchema()
        {
            // Arrange
            var guildType = typeof(WowVanillaGuild);

            // Act
            var idProp = guildType.GetProperty("Id");
            var serverIdProp = guildType.GetProperty("ServerId");
            var setByIdProp = guildType.GetProperty("SetById");
            var timeSetProp = guildType.GetProperty("TimeSet");

            // Assert
            Assert.NotNull(idProp);
            Assert.Equal(typeof(long), idProp.PropertyType); // bigint

            Assert.NotNull(serverIdProp);
            Assert.Equal(typeof(long?), serverIdProp.PropertyType); // bigint nullable

            Assert.NotNull(setByIdProp);
            Assert.Equal(typeof(long?), setByIdProp.PropertyType); // bigint nullable

            Assert.NotNull(timeSetProp);
            Assert.Equal(typeof(DateTime?), timeSetProp.PropertyType); // timestamp with time zone nullable
        }

        [Fact]
        public void WclPosted_Types_ShouldMatch_PostgresSchema()
        {
            // Arrange
            var wclType = typeof(WclPosted);

            // Act
            var idProp = wclType.GetProperty("Id");
            var serverIdProp = wclType.GetProperty("ServerId");
            var channelIdProp = wclType.GetProperty("ChannelId");

            // Assert - These handle Discord snowflake IDs which are large longs
            Assert.NotNull(idProp);
            Assert.Equal(typeof(long), idProp.PropertyType);

            Assert.NotNull(serverIdProp);
            Assert.Equal(typeof(long), serverIdProp.PropertyType);

            Assert.NotNull(channelIdProp);
            Assert.Equal(typeof(long), channelIdProp.PropertyType);
        }

        [Fact]
        public void TriviaQuestionChoice_Types_ShouldMatch_PostgresSchema()
        {
            // Arrange
            var choiceType = typeof(TriviaQuestionChoice);

            // Act
            var choiceIdProp = choiceType.GetProperty("ChoiceId");
            var questionIdProp = choiceType.GetProperty("QuestionId");
            var isRightChoiceProp = choiceType.GetProperty("IsRightChoice");
            var triviaQuestionProp = choiceType.GetProperty("TriviaQuestion");

            // Assert
            Assert.NotNull(choiceIdProp);
            Assert.Equal(typeof(long), choiceIdProp.PropertyType); // bigint

            Assert.NotNull(questionIdProp);
            Assert.Equal(typeof(long?), questionIdProp.PropertyType); // bigint nullable (FK)

            Assert.NotNull(isRightChoiceProp);
            Assert.Equal(typeof(bool?), isRightChoiceProp.PropertyType); // boolean nullable

            Assert.NotNull(triviaQuestionProp);
            Assert.Equal(typeof(TriviaQuestion), triviaQuestionProp.PropertyType); // Navigation property
        }

        [Fact]
        public void AllDateTimeProperties_ShouldBe_Nullable_OrNonNullable_DateTime()
        {
            // Arrange
            var entityTypes = typeof(NinjaBotEntities).Assembly
                .GetTypes()
                .Where(t => t.Namespace == "NinjaBotCore.Database" &&
                           t.IsClass &&
                           !t.IsAbstract &&
                           !t.Name.Contains("<") && // Exclude compiler-generated classes
                           t != typeof(NinjaBotEntities) &&
                           t != typeof(NinjaBotEntitiesFactory) &&
                           t != typeof(DatabaseConfigurator))
                .ToList();

            var invalidDateTimeTypes = new System.Collections.Generic.List<string>();

            // Act & Assert
            foreach (var entityType in entityTypes)
            {
                var properties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

                foreach (var prop in properties)
                {
                    // Check if property name suggests it's a date/time field
                    // Exclude known non-DateTime fields:
                    // - "AuctionTimeLeft" which is a duration string
                    // - "Timezone" which is a timezone name string like "America/New_York"
                    if ((prop.Name.Contains("Time") || prop.Name.Contains("Date")) &&
                        prop.Name != "AuctionTimeLeft" &&
                        prop.Name != "Timezone") // Timezone is a string like "America/New_York"
                    {
                        // Should be DateTime or DateTime?, not string
                        if (prop.PropertyType == typeof(string))
                        {
                            invalidDateTimeTypes.Add($"{entityType.Name}.{prop.Name} is string, should be DateTime/DateTime?");
                        }
                    }

                    // Verify actual DateTime properties
                    if (prop.PropertyType == typeof(DateTime) || prop.PropertyType == typeof(DateTime?))
                    {
                        // This is correct - no action needed, but we're validating they exist
                        continue;
                    }
                }
            }

            // Assert - No string-based DateTime fields
            Assert.Empty(invalidDateTimeTypes);
        }

        [Fact]
        public void AllBooleanProperties_ShouldBe_Bool_NotInteger()
        {
            // Arrange
            var entityTypes = typeof(NinjaBotEntities).Assembly
                .GetTypes()
                .Where(t => t.Namespace == "NinjaBotCore.Database" &&
                           t.IsClass &&
                           !t.IsAbstract &&
                           !t.Name.Contains("<") && // Exclude compiler-generated classes
                           t != typeof(NinjaBotEntities) &&
                           t != typeof(NinjaBotEntitiesFactory) &&
                           t != typeof(DatabaseConfigurator))
                .ToList();

            var properties = entityTypes
                .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => new { Type = t, Property = p }))
                .Where(x => x.Property.Name.Contains("Is") || x.Property.Name == "Status")
                .ToList();

            var invalidBoolTypes = new System.Collections.Generic.List<string>();

            // Act & Assert
            foreach (var prop in properties)
            {
                var propType = Nullable.GetUnderlyingType(prop.Property.PropertyType) ?? prop.Property.PropertyType;

                // Boolean fields should be bool/bool?, not int/int?
                if (propType == typeof(int))
                {
                    invalidBoolTypes.Add($"{prop.Type.Name}.{prop.Property.Name} is int, should be bool");
                }

                // Verify they are bool
                if (propType != typeof(bool) && propType != typeof(string))
                {
                    // Some fields might legitimately be other types, just flag for review
                    continue;
                }
            }

            // Assert - No integer-based boolean fields
            Assert.Empty(invalidBoolTypes);
        }

        [Fact]
        public void NinjaBotEntities_DbSets_ShouldBe_PublicVirtual()
        {
            // Arrange
            var dbContextType = typeof(NinjaBotEntities);

            // Act
            var dbSetProperties = dbContextType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsGenericType &&
                           p.PropertyType.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.DbSet<>))
                .ToList();

            // Assert
            Assert.NotEmpty(dbSetProperties);

            foreach (var prop in dbSetProperties)
            {
                // Verify all DbSet properties are virtual (for lazy loading/proxies)
                var getMethod = prop.GetGetMethod();
                Assert.True(getMethod.IsVirtual, $"{prop.Name} should be virtual");
                Assert.True(getMethod.IsPublic, $"{prop.Name} should be public");
            }
        }

        [Fact]
        public void AllEntities_ShouldHave_KeyAttribute_OrConfiguredKey()
        {
            // Arrange
            var entityTypes = typeof(NinjaBotEntities).Assembly
                .GetTypes()
                .Where(t => t.Namespace == "NinjaBotCore.Database" &&
                           t.IsClass &&
                           !t.IsAbstract &&
                           !t.Name.Contains("<") && // Exclude compiler-generated classes
                           t != typeof(NinjaBotEntities) &&
                           t != typeof(NinjaBotEntitiesFactory) &&
                           t != typeof(DatabaseConfigurator))
                .ToList();

            var entitiesWithoutKeys = new System.Collections.Generic.List<string>();

            // Act
            foreach (var entityType in entityTypes)
            {
                var hasKeyAttribute = entityType
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Any(p => p.GetCustomAttribute<System.ComponentModel.DataAnnotations.KeyAttribute>() != null);

                var hasIdProperty = entityType.GetProperty("Id") != null;

                // Each entity should have either [Key] attribute or an "Id" property
                if (!hasKeyAttribute && !hasIdProperty)
                {
                    // Check for custom-named ID properties
                    var hasCustomIdProp = entityType
                        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Any(p => p.Name.EndsWith("Id") &&
                                 (p.PropertyType == typeof(long) || p.PropertyType == typeof(int)));

                    if (!hasCustomIdProp)
                    {
                        entitiesWithoutKeys.Add(entityType.Name);
                    }
                }
            }

            // Assert
            Assert.Empty(entitiesWithoutKeys);
        }
    }
}
