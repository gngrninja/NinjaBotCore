using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NinjaBotCore.Models.Help
{
    public class HelpContent
    {
        [JsonPropertyName("categories")]
        public List<HelpCategory> Categories { get; set; }

        [JsonPropertyName("permission_badges")]
        public Dictionary<string, string> PermissionBadges { get; set; }

        [JsonPropertyName("metadata")]
        public HelpMetadata Metadata { get; set; }
    }

    public class HelpCategory
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("emoji")]
        public string Emoji { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("permission_level")]
        public string PermissionLevel { get; set; }

        [JsonPropertyName("commands")]
        public List<HelpCommand> Commands { get; set; }

        public HelpCategory Clone()
        {
            return new HelpCategory
            {
                Id = this.Id,
                Name = this.Name,
                Emoji = this.Emoji,
                Description = this.Description,
                PermissionLevel = this.PermissionLevel,
                Commands = new List<HelpCommand>(this.Commands)
            };
        }
    }

    public class HelpCommand
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("usage")]
        public string Usage { get; set; }

        [JsonPropertyName("example")]
        public string Example { get; set; }

        [JsonPropertyName("permission")]
        public string Permission { get; set; }

        [JsonPropertyName("permission_badge")]
        public string PermissionBadge { get; set; }

        [JsonPropertyName("parameters")]
        public List<HelpParameter> Parameters { get; set; } = new();
    }

    public class HelpParameter
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("required")]
        public bool Required { get; set; }

        [JsonPropertyName("choices")]
        public List<string> Choices { get; set; }
    }

    public class HelpMetadata
    {
        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("last_updated")]
        public string LastUpdated { get; set; }

        [JsonPropertyName("total_commands")]
        public int TotalCommands { get; set; }
    }
}
