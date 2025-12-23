using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

class Program
{
    private DiscordSocketClient _client = null!;
	private static string Token =>
    Environment.GetEnvironmentVariable("DISCORD_TOKEN")
    ?? throw new Exception("DISCORD_TOKEN not set");
    private const string PointsFile = "points.json";
    private const string PointGiversFile = "pointgivers.json";
    private const string UpgradesFile = "upgrades.json";
    private const string ChannelFile = "channels.json";

    private Dictionary<ulong, int> _points = new();
    private HashSet<ulong> _pointGiverRoles = new();
    private List<UpgradePath> _upgradePaths = new();
    private Dictionary<ulong, ulong> _announceChannels = new();

    class UpgradePath
    {
        public ulong OldRoleId { get; set; }
        public ulong NewRoleId { get; set; }
        public int RequiredPoints { get; set; }
    }

    static async Task Main() => await new Program().RunAsync();

    public async Task RunAsync()
    {
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents =
                GatewayIntents.Guilds |
                GatewayIntents.GuildMembers |
                GatewayIntents.GuildMessages |
                GatewayIntents.MessageContent
        });

        _client.Log += m => { Console.WriteLine(m); return Task.CompletedTask; };
        _client.Ready += OnReadyAsync;
        _client.MessageReceived += OnMessageAsync;

        LoadData();

        await _client.LoginAsync(TokenType.Bot, Token);
        await _client.StartAsync();
        await Task.Delay(-1);
    }

    // ================= READY =================

    private async Task OnReadyAsync()
    {
        Console.WriteLine($"Connected as {_client.CurrentUser}");
        foreach (var g in _client.Guilds)
            await g.DownloadUsersAsync();
    }

    // ================= MESSAGE HANDLER =================

    private async Task OnMessageAsync(SocketMessage raw)
    {
        if (raw.Author.IsBot) return;
        if (raw is not SocketUserMessage msg) return;
        if (msg.Channel is not SocketGuildChannel ch) return;

        var guild = ch.Guild;
        var content = msg.Content.Trim();

        if (content.StartsWith("!help")) await Help(msg);
        else if (content.StartsWith("!addpointgiver")) await AddPointGiver(msg);
        else if (content.StartsWith("!setchannel")) await SetChannel(msg, guild);
        else if (content.StartsWith("!givepoints")) await GivePoints(msg, guild);
        else if (content.StartsWith("!restorepoints")) await RestorePoints(msg, guild);
        else if (content.StartsWith("!points")) await ShowPoints(msg);
        else if (content.StartsWith("!showpoints")) await ShowLeaderboard(msg, guild);
        else if (content.StartsWith("!upgrade")) await SetupUpgrade(msg, guild);
        else if (content.StartsWith("!removeupgrade")) await RemoveUpgrade(msg);
    }

    // ================= COMMANDS =================

    private async Task Help(SocketUserMessage msg)
    {
        await msg.Channel.SendMessageAsync(
@"**Commands**
`!addpointgiver @Role`
`!setchannel`
`!givepoints @User [amount]`
`!restorepoints @User`
`!points @User`
`!showpoints [@Role]`
`!upgrade @OldRole @NewRole points`
`!removeupgrade @OldRole @NewRole`");
    }

    private async Task AddPointGiver(SocketUserMessage msg)
    {
        foreach (var r in msg.MentionedRoles)
            _pointGiverRoles.Add(r.Id);

        SavePointGivers();
        await msg.Channel.SendMessageAsync("✅ Point giver roles updated.");
    }

    private async Task SetChannel(SocketUserMessage msg, SocketGuild guild)
    {
        if (!IsPointGiver(msg.Author as SocketGuildUser))
        {
            await msg.Channel.SendMessageAsync("❌ Only point givers can set the channel.");
            return;
        }

        _announceChannels[guild.Id] = msg.Channel.Id;
        SaveChannels();

        await msg.Channel.SendMessageAsync("📢 Level-up announcements will be sent here.");
    }

    private async Task GivePoints(SocketUserMessage msg, SocketGuild guild)
    {
        if (!IsPointGiver(msg.Author as SocketGuildUser))
        {
            await msg.Channel.SendMessageAsync("❌ You cannot give points.");
            return;
        }

        int amount = 1;
        var parts = msg.Content.Split(' ');
        if (int.TryParse(parts.Last(), out int v)) amount = v;

        foreach (var u in msg.MentionedUsers)
        {
            _points[u.Id] = _points.GetValueOrDefault(u.Id) + amount;
            SavePoints();

            var member = guild.GetUser(u.Id);
            if (member != null)
                await RecalculateRolesAsync(member);
        }

        await msg.Channel.SendMessageAsync("✅ Points updated.");
    }

    private async Task RestorePoints(SocketUserMessage msg, SocketGuild guild)
    {
        if (!IsPointGiver(msg.Author as SocketGuildUser))
        {
            await msg.Channel.SendMessageAsync("❌ You cannot restore points.");
            return;
        }

        foreach (var u in msg.MentionedUsers)
        {
            _points[u.Id] = 0;
            SavePoints();

            var member = guild.GetUser(u.Id);
            if (member != null)
                await RecalculateRolesAsync(member);
        }

        await msg.Channel.SendMessageAsync("♻ Points reset to zero.");
    }

    private async Task ShowPoints(SocketUserMessage msg)
    {
        foreach (var u in msg.MentionedUsers)
        {
            int pts = _points.GetValueOrDefault(u.Id);
            await msg.Channel.SendMessageAsync($"**{u.Username}** has {pts} points.");
        }
    }

    private async Task ShowLeaderboard(SocketUserMessage msg, SocketGuild guild)
    {
        if (!IsPointGiver(msg.Author as SocketGuildUser))
        {
            await msg.Channel.SendMessageAsync("❌ Only point givers can use this.");
            return;
        }

        var role = msg.MentionedRoles.FirstOrDefault();

        var users = guild.Users
            .Where(u => role == null || u.Roles.Any(r => r.Id == role.Id))
            .Select(u => new { u.Username, u.Id, P = _points.GetValueOrDefault(u.Id) })
            .OrderByDescending(x => x.P)
            .Take(10);

        await msg.Channel.SendMessageAsync(
            "**Leaderboard**\n" +
            string.Join("\n", users.Select((u, i) =>
                $"{i + 1}. **{u.Username}** — {u.P}")));
    }

    // ================= UPGRADES =================

    private async Task SetupUpgrade(SocketUserMessage msg, SocketGuild guild)
    {
        var parts = msg.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var roleIds = parts.Where(p => p.StartsWith("<@&"))
            .Select(p => ulong.Parse(p.Trim('<', '@', '&', '>')))
            .ToList();

        if (roleIds.Count < 2 || !int.TryParse(parts.Last(), out int pts))
        {
            await msg.Channel.SendMessageAsync("Usage: !upgrade @OldRole @NewRole points");
            return;
        }

        _upgradePaths.RemoveAll(u =>
            u.OldRoleId == roleIds[0] &&
            u.NewRoleId == roleIds[1]);

        _upgradePaths.Add(new UpgradePath
        {
            OldRoleId = roleIds[0],
            NewRoleId = roleIds[1],
            RequiredPoints = pts
        });

        SaveUpgrades();
        await msg.Channel.SendMessageAsync("✅ Upgrade path set.");
    }

    private async Task RemoveUpgrade(SocketUserMessage msg)
    {
        var roles = msg.MentionedRoles.ToList();
        if (roles.Count < 2) return;

        _upgradePaths.RemoveAll(u =>
            u.OldRoleId == roles[0].Id &&
            u.NewRoleId == roles[1].Id);

        SaveUpgrades();
        await msg.Channel.SendMessageAsync("✅ Upgrade removed.");
    }

    // ================= CORE LOGIC =================

    private async Task RecalculateRolesAsync(SocketGuildUser user)
    {
        int pts = _points.GetValueOrDefault(user.Id);

        var allUpgradeRoles = _upgradePaths
            .SelectMany(u => new[] { u.OldRoleId, u.NewRoleId })
            .Distinct()
            .Select(id => user.Guild.GetRole(id))
            .Where(r => r != null)
            .ToList();

        var eligible = _upgradePaths
            .Where(u => pts >= u.RequiredPoints)
            .OrderByDescending(u => u.RequiredPoints)
            .FirstOrDefault();

        foreach (var r in allUpgradeRoles)
            if (user.Roles.Any(ur => ur.Id == r.Id))
                await user.RemoveRoleAsync(r);

        if (eligible != null)
        {
            var newRole = user.Guild.GetRole(eligible.NewRoleId);
            if (newRole != null)
            {
                await user.AddRoleAsync(newRole);
                await SendLevelMessage(user, newRole.Name);
            }
        }
    }

    private async Task SendLevelMessage(SocketGuildUser user, string roleName)
    {
        if (!_announceChannels.TryGetValue(user.Guild.Id, out ulong channelId))
            return;

        var channel = user.Guild.GetTextChannel(channelId);
        if (channel != null)
            await channel.SendMessageAsync(
                $"🎉 <@{user.Id}> is now **{roleName}**!");
    }

    private bool IsPointGiver(SocketGuildUser? u) =>
        u != null && u.Roles.Any(r => _pointGiverRoles.Contains(r.Id));

    // ================= STORAGE =================

    private void LoadData()
    {
        if (File.Exists(PointsFile))
            _points = JsonSerializer.Deserialize<Dictionary<ulong, int>>(File.ReadAllText(PointsFile)) ?? new();
        if (File.Exists(PointGiversFile))
            _pointGiverRoles = JsonSerializer.Deserialize<HashSet<ulong>>(File.ReadAllText(PointGiversFile)) ?? new();
        if (File.Exists(UpgradesFile))
            _upgradePaths = JsonSerializer.Deserialize<List<UpgradePath>>(File.ReadAllText(UpgradesFile)) ?? new();
        if (File.Exists(ChannelFile))
            _announceChannels = JsonSerializer.Deserialize<Dictionary<ulong, ulong>>(File.ReadAllText(ChannelFile)) ?? new();
    }

    private void SavePoints() =>
        File.WriteAllText(PointsFile, JsonSerializer.Serialize(_points));
    private void SavePointGivers() =>
        File.WriteAllText(PointGiversFile, JsonSerializer.Serialize(_pointGiverRoles));
    private void SaveUpgrades() =>
        File.WriteAllText(UpgradesFile, JsonSerializer.Serialize(_upgradePaths));
    private void SaveChannels() =>
        File.WriteAllText(ChannelFile, JsonSerializer.Serialize(_announceChannels));
}
