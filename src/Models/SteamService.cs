
using CounterStrikeSharp.API.Core;
using KitsuneSteamRestrict;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Steamworks;

public class SteamUserInfo
{
	public DateTime SteamAccountAge { get; set; }
	public int SteamLevel { get; set; }
	public int CS2Level { get; set; }
	public int CS2Playtime { get; set; }
	public bool IsPrivate { get; set; }
	public bool HasPrime { get; set; }
	public bool IsTradeBanned { get; set; }
	public bool IsVACBanned { get; set; }
	public bool IsGameBanned { get; set; }
	public bool IsInSteamGroup { get; set; }
}

public class SteamService
{
	private readonly HttpClient _httpClient;
	private readonly string _steamWebAPIKey;
	private readonly PluginConfig _config;
	private readonly ILogger _logger;
	public SteamUserInfo? UserInfo = null;

	public SteamService(SteamRestrictPlugin plugin)
	{
		_httpClient = plugin.Client;
		_config = plugin.Config;
		_logger = plugin.Logger;
		_steamWebAPIKey = _config.SteamWebAPI;
	}

	public async Task FetchSteamUserInfoAsync(nint handle, ulong authorizedSteamID)
	{
		CSteamID cSteamID = new CSteamID(authorizedSteamID);

		UserInfo = new SteamUserInfo
		{
			HasPrime = SteamGameServer.UserHasLicenseForApp(cSteamID, (AppId_t)624820) == EUserHasLicenseForAppResult.k_EUserHasLicenseResultHasLicense
					|| SteamGameServer.UserHasLicenseForApp(cSteamID, (AppId_t)54029) == EUserHasLicenseForAppResult.k_EUserHasLicenseResultHasLicense,
			CS2Level = new CCSPlayerController_InventoryServices(handle).PersonaDataPublicLevel
		};

		string steamId = authorizedSteamID.ToString();

		UserInfo.CS2Playtime = await FetchCS2PlaytimeAsync(steamId) / 60;
		UserInfo.SteamLevel = await FetchSteamLevelAsync(steamId);
		await FetchProfilePrivacyAsync(steamId, UserInfo);
		await FetchTradeBanStatusAsync(steamId, UserInfo);
		await FetchGameBanStatusAsync(steamId, UserInfo);
		await FetchSteamGroupMembershipAsync(steamId, UserInfo);
	}

	private async Task<int> FetchCS2PlaytimeAsync(string steamId)
	{
		var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={_steamWebAPIKey}&steamid={steamId}&format=json";
		var json = await GetApiResponseAsync(url);
		return json != null ? ParseCS2Playtime(json) : 0;
	}

	private async Task<int> FetchSteamLevelAsync(string steamId)
	{
		var url = $"http://api.steampowered.com/IPlayerService/GetSteamLevel/v1/?key={_steamWebAPIKey}&steamid={steamId}";
		var json = await GetApiResponseAsync(url);
		return json != null ? ParseSteamLevel(json) : 0;
	}

	private async Task FetchProfilePrivacyAsync(string steamId, SteamUserInfo userInfo)
	{
		var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={_steamWebAPIKey}&steamids={steamId}";
		var json = await GetApiResponseAsync(url);
		if (json != null) ParseSteamUserInfo(json, userInfo);
	}

	private async Task FetchTradeBanStatusAsync(string steamId, SteamUserInfo userInfo)
	{
		var url = $"https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/?key={_steamWebAPIKey}&steamids={steamId}";
		var json = await GetApiResponseAsync(url);
		if (json != null)
		{
			ParseTradeBanStatus(json, userInfo);
			ParseVACBanStatus(json, userInfo);
		}
	}

	private async Task FetchGameBanStatusAsync(string steamId, SteamUserInfo userInfo)
	{
		var url = $"https://api.steampowered.com/ISteamUser/GetUserGameBan/v1/?key={_steamWebAPIKey}&steamids={steamId}";
		var json = await GetApiResponseAsync(url);
		if (json != null) ParseGameBanStatus(json, userInfo);
	}

	private async Task FetchSteamGroupMembershipAsync(string steamId, SteamUserInfo userInfo)
	{
		if (!string.IsNullOrEmpty(_config.SteamGroupID))
		{
			var url = $"http://api.steampowered.com/ISteamUser/GetUserGroupList/v1/?key={_steamWebAPIKey}&steamid={steamId}";
			var json = await GetApiResponseAsync(url);

			userInfo.IsInSteamGroup = false;
			if (json != null)
			{
				JObject jsonObj = JObject.Parse(json);
				JToken? groups = jsonObj["response"]?["groups"];
				if (groups != null)
				{
					userInfo.IsInSteamGroup = groups.Any(group => (group["gid"]?.ToString() ?? "") == _config.SteamGroupID);
				}
			}
		}
		else
		{
			userInfo.IsInSteamGroup = true;
		}
	}

	private async Task<string?> GetApiResponseAsync(string url)
	{
		try
		{
			var response = await _httpClient.GetAsync(url);
			if (response.IsSuccessStatusCode)
			{
				return await response.Content.ReadAsStringAsync();
			}
		}
		catch (Exception e)
		{
			_logger.LogError($"An error occurred while fetching API response: {e.Message}");
		}
		return null;
	}

	private int ParseCS2Playtime(string json)
	{
		JObject data = JObject.Parse(json);
		JToken? game = data["response"]?["games"]?.FirstOrDefault(x => x["appid"]?.Value<int>() == 730);
		return game?["playtime_forever"]?.Value<int>() ?? 0;
	}

	private int ParseSteamLevel(string json)
	{
		JObject data = JObject.Parse(json);
		return (int)(data["response"]?["player_level"] ?? 0);
	}

	private void ParseSteamUserInfo(string json, SteamUserInfo userInfo)
	{
		JObject data = JObject.Parse(json);
		JToken? player = data["response"]?["players"]?.FirstOrDefault();
		if (player != null)
		{
			userInfo.IsPrivate = player["communityvisibilitystate"]?.ToObject<int?>() != 3;
			int? timeCreated = player["timecreated"]?.ToObject<int?>();
			userInfo.SteamAccountAge = timeCreated.HasValue
				? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(timeCreated.Value)
				: DateTime.Now;
		}
	}

	private void ParseTradeBanStatus(string json, SteamUserInfo userInfo)
	{
		JObject data = JObject.Parse(json);
		JToken? playerBan = data["players"]?.FirstOrDefault();
		userInfo.IsTradeBanned = playerBan != null && (bool)(playerBan["CommunityBanned"] ?? false);
	}

	private void ParseGameBanStatus(string json, SteamUserInfo userInfo)
	{
		JObject data = JObject.Parse(json);
		JToken? userGameBan = data["players"]?.FirstOrDefault();
		userInfo.IsGameBanned = userGameBan != null && (bool)(userGameBan["IsGameBanned"] ?? false);
	}

	private void ParseVACBanStatus(string json, SteamUserInfo userInfo)
	{
		JObject data = JObject.Parse(json);
		JToken? userGameBan = data["players"]?.FirstOrDefault();
		userInfo.IsVACBanned = userGameBan != null && (bool)(userGameBan["VACBanned"] ?? false);
	}
}
