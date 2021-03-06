﻿using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VainBot.Classes;

namespace VainBot.Services
{
    public class TwitchService
    {
        readonly DiscordSocketClient _discord;
        readonly HttpClient _httpClient;
        readonly IConfiguration _config;
        IServiceProvider _provider;

        string _accessToken;
        List<TwitchStreamToCheck> _streamsToCheck;
        List<TwitchLiveStream> _liveStreams;

        Timer _accessTokenTimer;
        Timer _pollTimer;

        public TwitchService(
            DiscordSocketClient discord,
            HttpClient httpClient,
            IConfiguration config,
            IServiceProvider provider)
        {
            _discord = discord;
            _httpClient = httpClient;
            _config = config;
            _provider = provider;
        }

        public async Task InitializeAsync(IServiceProvider provider)
        {
            _provider = provider;

            using (var db = _provider.GetRequiredService<VbContext>())
            {
                _accessToken = await db.KeyValues.GetValueAsync(KeyValueKeys.TwitchAccessToken);
                _streamsToCheck = await db.StreamsToCheck.ToListAsync();
                _liveStreams = await db.TwitchLiveStreams.ToListAsync();
            }

            if (string.IsNullOrEmpty(_accessToken))
                await RefreshAccessTokenAsync();

            _pollTimer = new Timer(async (e) => await CheckStreamsAsync(), null, 0, 60000);
        }

        /// <summary>
        /// Checks all streams currently in memory. The Twitch API supports up to 100 user IDs per request.
        /// </summary>
        /// <returns></returns>
        public async Task CheckStreamsAsync()
        {
            if (_streamsToCheck == null || _streamsToCheck.Count == 0)
                return;

            var userIds = _streamsToCheck
                .GroupBy(s => s.TwitchId)
                .Select(s => s.First().TwitchId);

            var userIdStreamString = string.Join("&user_id=", userIds);

            var streamRequest = GetRequestMessage();
            streamRequest.RequestUri = new Uri($"https://api.twitch.tv/helix/streams?user_id={userIdStreamString}");
            streamRequest.Method = HttpMethod.Get;

            var streamResponse = await _httpClient.SendAsync(streamRequest);
            try
            {
                await ThrowIfResponseInvalidAsync(streamResponse);
            }
            catch
            {
                return;
            }

            var liveStreams =
                JsonConvert.DeserializeObject<TwitchStreamResponse>(await streamResponse.Content.ReadAsStringAsync()).Streams;

            var games = new List<TwitchGame>();
            var users = new List<TwitchUser>();

            if (liveStreams.Count > 0)
            {
                var gameIds = liveStreams
                    .GroupBy(s => s.GameId)
                    .Select(s => s.First().GameId);

                var gameIdString = string.Join("&id=", gameIds);

                var gameRequest = GetRequestMessage();
                gameRequest.RequestUri = new Uri($"https://api.twitch.tv/helix/games?id={gameIdString}");
                gameRequest.Method = HttpMethod.Get;
                var gameResponse = await _httpClient.SendAsync(gameRequest);
                try
                {
                    await ThrowIfResponseInvalidAsync(gameResponse);
                }
                catch
                {
                    return;
                }

                games = JsonConvert.DeserializeObject<TwitchGameResponse>(await gameResponse.Content.ReadAsStringAsync()).Games;

                userIds = liveStreams
                    .GroupBy(s => s.UserId)
                    .Select(s => s.First().UserId);

                var userIdUserString = string.Join("&id=", userIds);

                var userRequest = GetRequestMessage();
                userRequest.RequestUri = new Uri($"https://api.twitch.tv/helix/users?id={userIdUserString}");
                userRequest.Method = HttpMethod.Get;
                var userResponse = await _httpClient.SendAsync(userRequest);
                try
                {
                    await ThrowIfResponseInvalidAsync(userResponse);
                }
                catch
                {
                    return;
                }

                users = JsonConvert.DeserializeObject<TwitchUserResponse>(await userResponse.Content.ReadAsStringAsync()).Users;
            }

            var newlyOnline = new List<TwitchLiveStream>();
            var newlyOffline = new List<TwitchLiveStream>(_liveStreams);
            var stillOnline = new List<TwitchLiveStream>();

            foreach (var stream in liveStreams)
            {
                // first, check if the streamer WAS online as of the last check.
                var existingStream = _liveStreams.Find(l => l.TwitchUserId == stream.UserId || l.TwitchStreamId == stream.Id);

                var user = users.Find(u => u.Id == stream.UserId);
                var game = games.Find(g => g.Id == stream.GameId);

                // if the streamer WAS online and STILL IS online, then they're not newly offline.
                // they also belong in stillOnline.
                if (existingStream != null)
                {
                    newlyOffline.RemoveAll(t => t.TwitchUserId == stream.UserId || t.TwitchStreamId == stream.Id);

                    existingStream.TwitchStreamId = stream.Id;
                    existingStream.TwitchDisplayName = user.DisplayName;
                    existingStream.GameId = stream.GameId;
                    existingStream.GameName = game.Name;
                    existingStream.ThumbnailUrl = stream.ThumbnailUrl;
                    existingStream.ViewerCount = stream.ViewerCount;
                    existingStream.ProfileImageUrl = user.ProfileImageUrl;

                    stillOnline.Add(existingStream);
                }

                // the streamer WAS NOT online, so they're newly online. also verify that it's not a vodcast.
                else if (stream.Type != TwitchStreamType.Vodcast && !_liveStreams.Any(l => l.TwitchStreamId == stream.Id))
                {
                    var liveStream = new TwitchLiveStream
                    {
                        StartedAt = stream.StartedAt,
                        TwitchStreamId = stream.Id,
                        TwitchUserId = stream.UserId,
                        TwitchLogin = user.Login,
                        TwitchDisplayName = user.DisplayName,
                        GameId = stream.GameId,
                        Title = stream.Title,
                        ViewerCount = stream.ViewerCount,
                        GameName = game.Name,
                        ThumbnailUrl = stream.ThumbnailUrl,
                        ProfileImageUrl = user.ProfileImageUrl
                    };

                    newlyOnline.Add(liveStream);
                    _liveStreams.Add(liveStream);
                }
            }

            var actuallyNewlyOffline = new List<TwitchLiveStream>();

            foreach (var n in newlyOffline)
            {
                if (!n.FirstOfflineAt.HasValue)
                {
                    n.FirstOfflineAt = DateTimeOffset.UtcNow;
                }
                else if (n.FirstOfflineAt.Value <= DateTimeOffset.UtcNow.AddMinutes(-5))
                {
                    _liveStreams.Remove(n);
                    actuallyNewlyOffline.Add(n);
                }
            }

            using (var db = _provider.GetRequiredService<VbContext>())
            {
                db.TwitchLiveStreams.AddRange(newlyOnline);
                db.TwitchLiveStreams.UpdateRange(stillOnline);
                db.TwitchLiveStreams.UpdateRange(newlyOffline);
                db.TwitchLiveStreams.RemoveRange(actuallyNewlyOffline);

                await db.SaveChangesAsync();
            }

            await HandleNewlyOnlineStreamsAsync(newlyOnline);
            // await HandleStillOnlineStreamsAsync(stillOnline);
            await HandleNewlyOfflineStreamsAsync(actuallyNewlyOffline);
        }

        public async Task HandleNewlyOnlineStreamsAsync(List<TwitchLiveStream> streams)
        {
            if (streams.Count == 0)
                return;

            var toCheckUpdated = new List<TwitchStreamToCheck>();

            foreach (var toCheck in _streamsToCheck)
            {
                var stream = streams.Find(s => s.TwitchUserId == toCheck.TwitchId);
                if (stream == null)
                    continue;

                Embed embed = null;
                if (toCheck.IsEmbedded)
                    embed = CreateEmbed(stream);

                var channel = _discord.GetChannel((ulong)toCheck.ChannelId) as SocketTextChannel;
                if (channel == null)
                {
                    await RemoveStreamByIdAsync(toCheck.Id);
                    Console.Error.WriteLine($"Channel does not exist: {toCheck.ChannelId} in guild {toCheck.GuildId} for streamer {toCheck.Username}. Removing entry.");
                    return;
                }

                var message = await channel.SendMessageAsync(toCheck.MessageToPost/*, embed: embed*/);
                toCheck.CurrentMessageId = (long)message.Id;

                toCheckUpdated.Add(toCheck);
            }

            using (var db = _provider.GetRequiredService<VbContext>())
            {
                db.StreamsToCheck.UpdateRange(toCheckUpdated);
                await db.SaveChangesAsync();
            }
        }

        public async Task HandleStillOnlineStreamsAsync(List<TwitchLiveStream> streams)
        {
            if (streams.Count == 0)
                return;

            foreach (var toCheck in _streamsToCheck.Where(s => s.IsEmbedded && s.CurrentMessageId.HasValue))
            {
                var stream = streams.Find(s => s.TwitchUserId == toCheck.TwitchId);
                if (stream == null)
                    continue;

                var embed = CreateEmbed(stream);
                var channel = _discord.GetChannel((ulong)toCheck.ChannelId) as SocketTextChannel;
                var message = await channel.GetMessageAsync((ulong)toCheck.CurrentMessageId.Value) as RestUserMessage;
                await message.ModifyAsync(m => m.Embed = embed);
            }
        }

        public async Task HandleNewlyOfflineStreamsAsync(List<TwitchLiveStream> streams)
        {
            if (streams.Count == 0)
                return;

            var toCheckUpdated = new List<TwitchStreamToCheck>();

            foreach (var toCheck in _streamsToCheck.Where(s => s.CurrentMessageId.HasValue))
            {
                var stream = streams.Find(s => s.TwitchUserId == toCheck.TwitchId);
                if (stream == null)
                    continue;

                if (toCheck.IsDeleted)
                {
                    var channel = _discord.GetChannel((ulong)toCheck.ChannelId) as SocketTextChannel;
                    var message = await channel.GetMessageAsync((ulong)toCheck.CurrentMessageId.Value) as RestUserMessage;
                    await message.DeleteAsync();
                }

                toCheck.CurrentMessageId = null;
                toCheckUpdated.Add(toCheck);
            }

            using (var db = _provider.GetRequiredService<VbContext>())
            {
                db.StreamsToCheck.UpdateRange(toCheckUpdated);
                await db.SaveChangesAsync();
            }
        }

        Embed CreateEmbed(TwitchLiveStream stream)
        {
            var now = DateTime.UtcNow;
            var cacheBuster =
                now.Year.ToString() +
                now.Month.ToString() +
                now.Day.ToString() +
                now.Hour.ToString() +
                ((now.Minute / 10) % 10).ToString();

            var author = new EmbedAuthorBuilder
            {
                Name = stream.TwitchDisplayName,
                IconUrl = stream.ProfileImageUrl,
                Url = $"https://www.twitch.tv/{stream.TwitchLogin}"
            };

            var playingField = new EmbedFieldBuilder
            {
                Name = "Playing",
                Value = stream.GameName,
                IsInline = true
            };

            var viewerCountField = new EmbedFieldBuilder
            {
                Name = "Viewers",
                Value = stream.ViewerCount,
                IsInline = true
            };

            var embed = new EmbedBuilder
            {
                // https://www.twitch.tv/p/brand/
                Color = new Color(100, 65, 164),
                Author = author,
                Title = stream.Title,
                ImageUrl = stream.ThumbnailUrl.Replace("{width}", "640").Replace("{height}", "360") + $"?{cacheBuster}",
                Url = author.Url
            };

            embed.AddField(playingField);
            embed.AddField(viewerCountField);

            return embed.Build();
        }

        /// <summary>
        /// Gets all streams for the given guild
        /// </summary>
        /// <param name="guildId">ID of the guild</param>
        /// <returns>List of streams</returns>
        public List<TwitchStreamToCheck> GetStreamsByGuild(ulong guildId)
        {
            return _streamsToCheck
                .Where(s => s.GuildId == (long)guildId)
                .ToList();
        }

        /// <summary>
        /// Adds a stream to be checked
        /// </summary>
        /// <param name="stream">Stream to add</param>
        public async Task AddStreamAsync(TwitchStreamToCheck stream)
        {
            _streamsToCheck.Add(stream);

            using (var db = _provider.GetRequiredService<VbContext>())
            {
                db.StreamsToCheck.Add(stream);
                await db.SaveChangesAsync();
            }
        }

        /// <summary>
        /// Removes a stream that's currently being checked
        /// </summary>
        /// <param name="id">ID of the entry to remove</param>
        public async Task RemoveStreamByIdAsync(int id)
        {
            var stream = _streamsToCheck.Find(s => s.Id == id);
            if (stream == null)
                return;

            _streamsToCheck.Remove(stream);

            using (var db = _provider.GetRequiredService<VbContext>())
            {
                var s = await db.StreamsToCheck.FindAsync(id);
                if (s != null)
                {
                    db.StreamsToCheck.Remove(s);
                    await db.SaveChangesAsync();
                }
            }
        }

        /// <summary>
        /// Gets the current access token stored in memory.
        /// </summary>
        /// <returns>Twitch access token</returns>
        public string GetAccessToken()
        {
            return _accessToken;
        }

        /// <summary>
        /// Gets an HttpRequestMessage with credentials specified that can be used to query the Twitch API.
        /// </summary>
        /// <returns>HttpRequestMessage</returns>
        public HttpRequestMessage GetRequestMessage()
        {
            var request = new HttpRequestMessage();
            request.Headers.Add("Client-ID", _config["twitch_client_id"]);
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");
            return request;
        }

        /// <summary>
        /// Refreshes the access token.
        /// </summary>
        public async Task RefreshAccessTokenAsync()
        {
            if (_accessTokenTimer != null)
            {
                _accessTokenTimer.Dispose();
                _accessTokenTimer = null;
            }

            var url = QueryHelpers.AddQueryString(
                "https://api.twitch.tv/kraken/oauth2/token",
                new Dictionary<string, string>
                {
                    ["client_id"] = _config["twitch_client_id"],
                    ["client_secret"] = _config["twitch_client_secret"],
                    ["grant_type"] = "client_credentials"
                });

            var tokenResponse = await _httpClient.PostAsync(url, null);
            await ThrowIfResponseInvalidAsync(tokenResponse);

            var token = JsonConvert.DeserializeObject<TwitchTokenResponse>(await tokenResponse.Content.ReadAsStringAsync());
            _accessToken = token.AccessToken;

            // schedule the token refresh 7 days early
            _accessTokenTimer = new Timer(
                async (e) => await RefreshAccessTokenAsync(),
                null,
                TimeSpan.FromSeconds(token.ExpiresInSeconds - 604800),
                TimeSpan.FromMilliseconds(-1));
        }

        /// <summary>
        /// Throws an InvalidOperationException for the provided response if
        /// the response was an error.
        /// </summary>
        /// <param name="response">HttpResponseMessage to check</param>
        public async Task ThrowIfResponseInvalidAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
                return;

            var body = await response.Content.ReadAsStringAsync();
            var error = JsonConvert.DeserializeObject<TwitchErrorResponse>(body);

            throw new InvalidOperationException(
                $"Twitch token refresh failed with error code {error.Status}, {error.Error}. " +
                $"Message: {error.Message}");
        }

        /// <summary>
        /// Gets the display name and ID of the given Twitch username.
        /// </summary>
        /// <param name="username">Username to look up</param>
        /// <returns>ID and display name of user. ID will be "-1" on error.</returns>
        public async Task<(string Id, string DisplayName)> GetUserIdAsync(string username)
        {
            var request = GetRequestMessage();
            request.RequestUri = new Uri($"https://api.twitch.tv/helix/users?login={username}");
            request.Method = HttpMethod.Get;

            var response = await _httpClient.SendAsync(request);
            try
            {
                await ThrowIfResponseInvalidAsync(response);
            }
            catch
            {
                return ("-1", "An error occurred when getting the ID. Please try again, or yell at vaindil.");
            }

            var users = JsonConvert.DeserializeObject<TwitchUserResponse>(await response.Content.ReadAsStringAsync());
            if (users.Users.Count == 0)
                return ("-1", $"The user **{username}** does not exist.");

            return (users.Users[0].Id, users.Users[0].DisplayName);
        }
    }
}
