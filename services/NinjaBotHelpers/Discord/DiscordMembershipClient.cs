namespace NinjaBotHelpers.Discord;

public enum GuildMembership { Unknown, Present, Absent }
public interface IDiscordMembershipClient
{
    Task<GuildMembership> CheckAsync(long serverId, CancellationToken cancellationToken);
}

public sealed class DiscordMembershipClient(HttpClient httpClient) : IDiscordMembershipClient
{
    public async Task<GuildMembership> CheckAsync(long serverId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (serverId <= 0) return GuildMembership.Unknown;
        try
        {
            using var response = await httpClient.GetAsync(
                $"https://discord.com/api/v10/guilds/{serverId}", cancellationToken);
            // Never infer absence from permissions, rate limits, authentication or outages.
            if (response.StatusCode != System.Net.HttpStatusCode.OK &&
                response.StatusCode != System.Net.HttpStatusCode.NotFound)
                return GuildMembership.Unknown;
            using var body = System.Text.Json.JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return GuildMembership.Unknown;
            if (response.StatusCode == System.Net.HttpStatusCode.OK &&
                root.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String &&
                id.GetString() == serverId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                return GuildMembership.Present;
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound &&
                root.TryGetProperty("code", out var code) && code.ValueKind == System.Text.Json.JsonValueKind.Number &&
                code.TryGetInt32(out var value) && value == 10004)
                return GuildMembership.Absent;
        }
        catch (HttpRequestException) { }
        catch (System.Text.Json.JsonException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return GuildMembership.Unknown;
    }
}
