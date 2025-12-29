using Discord;
using Discord.WebSocket;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Net;

Task.Run(() =>
{
    HttpListener listener = new HttpListener();
    listener.Prefixes.Add("http://*:8080/");
    listener.Start();
    Console.WriteLine("Listening on port 8080...");
    while (true) Thread.Sleep(1000);
});

public class Program
{
    private DiscordSocketClient _client = null!;
    private InteractionService _interactions = null!;
    private IServiceProvider _services = null!;

    private static string Token =>
        Environment.GetEnvironmentVariable("DISCORD_TOKEN")
        ?? throw new Exception("DISCORD_TOKEN not set");

    private const string PointsFile = "points.json";
    private const string PointGiversFile = "pointgivers.json";
    private const string UpgradesFile = "upgrades.json";
    private const string AnnounceFile = "announcechannels.json";
    private const string CommandFile = "commandchannels.json";

    public Dictionary<ulong, int> _points = new();
    public HashSet<ulong> _pointGiverRoles = new();
    public List<UpgradePath> _upgradePaths = new();
    public Dictionary<ulong, ulong> _announceChannels = new();
    public Dictionary<ulong, ulong> _commandChannels = new();

    public class UpgradePath
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

        _interactions = new InteractionService(_client.Rest);

        _services = new ServiceCollection()
            .AddSingleton(this) // pass Program instance to slash modules
            .BuildServiceProvider();

        _client.Ready += OnReadyAsync;
        _client.MessageReceived += OnMessageAsync;
        _client.InteractionCreated += async interaction =>
        {
            var ctx = new SocketInteractionContext(_client, interaction);
            await _interactions.ExecuteCommandAsync(ctx, _services);
        };

        LoadData();

        await _client.LoginAsync(TokenType.Bot, Token);
        await _client.StartAsync();
        await Task.Delay(-1);
    }

    private async Task OnReadyAsync()
    {
        await _interactions.AddModuleAsync<SlashCommands>(_services);
        await _interactions.RegisterCommandsGloballyAsync();
        Console.WriteLine("Bot ready.");
    }

    // ================= PERMISSIONS =================
    public bool IsAdmin(SocketGuildUser u) =>
        u.GuildPermissions.Administrator;

    public bool IsPointGiver(SocketGuildUser? u) =>
        u != null && u.Roles.Any(r => _pointGiverRoles.Contains(r.Id));

    public bool IsAdminOrPointGiver(SocketGuildUser u) =>
        IsAdmin(u) || IsPointGiver(u);

    public bool CanUseCommands(SocketGuildUser user, SocketGuildChannel channel)
    {
        if (IsAdminOrPointGiver(user)) return true;
        return _commandChannels.TryGetValue(user.Guild.Id, out var cmd) && channel.Id == cmd;
    }

    // ================= MESSAGE COMMAND HANDLER =================
    private async Task OnMessageAsync(SocketMessage raw)
    {
        if (raw.Author.IsBot) return;
        if (raw is not SocketUserMessage msg) return;
        if (msg.Channel is not SocketGuildChannel ch) return;

        var user = msg.Author as SocketGuildUser;
        if (user == null) return;

        // Wrong-channel auto-delete
        if (msg.Content.StartsWith("!") && !CanUseCommands(user, ch))
        {
            await msg.DeleteAsync();
            if (_commandChannels.TryGetValue(user.Guild.Id, out var cmd))
                await user.SendMessageAsync($"❌ Commands can only be used in <#{cmd}>");
            return;
        }

        var content = msg.Content.Trim();

        if (content.StartsWith("!points"))
            await msg.Channel.SendMessageAsync($"{user.Username} has {_points.GetValueOrDefault(user.Id)} points.");

        else if (content.StartsWith("!givepoints"))
            await GivePoints(msg, user.Guild);

        else if (content.StartsWith("!restorepoints"))
            await RestorePoints(msg, user.Guild);

        else if (content.StartsWith("!showpoints"))
            await ShowLeaderboard(msg, user.Guild);

        else if (content.StartsWith("!upgrade"))
            await SetupUpgrade(msg);

        else if (content.StartsWith("!removeupgrade"))
            await RemoveUpgrade(msg);

        else if (content.StartsWith("!addpointgiver"))
            await AddPointGiver(msg);

        else if (content.StartsWith("!setcommandchannel"))
            await SetCommandChannel(msg);

        else if (content.StartsWith("!setannouncechannel"))
            await SetAnnounceChannel(msg);
    }

    // ================= CORE LOGIC =================
    public async Task RecalculateRolesAsync(SocketGuildUser user)
    {
        int pts = _points.GetValueOrDefault(user.Id);

        foreach (var up in _upgradePaths)
        {
            var oldRole = user.Guild.GetRole(up.OldRoleId);
            var newRole = user.Guild.GetRole(up.NewRoleId);
            if (oldRole != null) await user.RemoveRoleAsync(oldRole);
            if (newRole != null) await user.RemoveRoleAsync(newRole);
        }

        var eligible = _upgradePaths
            .Where(u => pts >= u.RequiredPoints)
            .OrderByDescending(u => u.RequiredPoints)
            .FirstOrDefault();

        if (eligible != null)
        {
            var newRole = user.Guild.GetRole(eligible.NewRoleId);
            if (newRole != null)
            {
                await user.AddRoleAsync(newRole);
                await SendLevelMessage(user, newRole.Name);
            }
        }
        else
        {
            foreach (var up in _upgradePaths)
            {
                var oldRole = user.Guild.GetRole(up.OldRoleId);
                if (oldRole != null) await user.AddRoleAsync(oldRole);
            }
        }
    }

    private async Task SendLevelMessage(SocketGuildUser user, string role)
    {
        if (_announceChannels.TryGetValue(user.Guild.Id, out var chId))
        {
            var ch = user.Guild.GetTextChannel(chId);
            if (ch != null)
                await ch.SendMessageAsync($"🎉 <@{user.Id}> is now **{role}**!");
        }
    }

    // ================= COMMAND IMPLEMENTATIONS =================
    private async Task GivePoints(SocketUserMessage msg, SocketGuild guild)
    {
        var user = msg.Author as SocketGuildUser;
        if (!IsAdminOrPointGiver(user)) return;

        int amount = int.TryParse(msg.Content.Split(' ').Last(), out int v) ? v : 1;

        foreach (var u in msg.MentionedUsers)
        {
            _points[u.Id] = _points.GetValueOrDefault(u.Id) + amount;
            SavePoints();
            var m = guild.GetUser(u.Id);
            if (m != null) await RecalculateRolesAsync(m);
        }
    }

    private async Task RestorePoints(SocketUserMessage msg, SocketGuild guild)
    {
        var user = msg.Author as SocketGuildUser;
        if (!IsAdminOrPointGiver(user)) return;

        foreach (var u in msg.MentionedUsers)
        {
            _points[u.Id] = 0;
            SavePoints();
            var m = guild.GetUser(u.Id);
            if (m != null) await RecalculateRolesAsync(m);
        }
    }

    private async Task ShowLeaderboard(SocketUserMessage msg, SocketGuild guild)
    {
        var user = msg.Author as SocketGuildUser;
        if (!IsAdminOrPointGiver(user)) return;

        var users = guild.Users
            .Select(u => new { u.Username, P = _points.GetValueOrDefault(u.Id) })
            .OrderByDescending(x => x.P)
            .Take(10);

        await msg.Channel.SendMessageAsync(
            "**Leaderboard**\n" +
            string.Join("\n", users.Select((u, i) =>
                $"{i + 1}. **{u.Username}** — {u.P}")));
    }

    private async Task SetupUpgrade(SocketUserMessage msg)
    {
        var user = msg.Author as SocketGuildUser;
        if (!IsAdminOrPointGiver(user)) return;

        var roles = msg.MentionedRoles.ToList();
        var parts = msg.Content.Split(' ');
        if (roles.Count < 2 || !int.TryParse(parts.Last(), out int pts)) return;

        _upgradePaths.RemoveAll(u =>
            u.OldRoleId == roles[0].Id &&
            u.NewRoleId == roles[1].Id);

        _upgradePaths.Add(new UpgradePath
        {
            OldRoleId = roles[0].Id,
            NewRoleId = roles[1].Id,
            RequiredPoints = pts
        });

        SaveUpgrades();
        await msg.Channel.SendMessageAsync("✅ Upgrade path set.");
    }

    private async Task RemoveUpgrade(SocketUserMessage msg)
    {
        var user = msg.Author as SocketGuildUser;
        if (!IsAdminOrPointGiver(user)) return;

        var roles = msg.MentionedRoles.ToList();
        if (roles.Count < 2) return;

        _upgradePaths.RemoveAll(u =>
            u.OldRoleId == roles[0].Id &&
            u.NewRoleId == roles[1].Id);

        SaveUpgrades();
        await msg.Channel.SendMessageAsync("✅ Upgrade removed.");
    }

    private async Task AddPointGiver(SocketUserMessage msg)
    {
        var user = msg.Author as SocketGuildUser;
        if (!IsAdmin(user)) return;

        foreach (var r in msg.MentionedRoles)
            _pointGiverRoles.Add(r.Id);

        SavePointGivers();
        await msg.Channel.SendMessageAsync("✅ Point giver role added.");
    }

    private async Task SetCommandChannel(SocketUserMessage msg)
    {
        var user = msg.Author as SocketGuildUser;
        if (!IsAdmin(user)) return;

        _commandChannels[user.Guild.Id] = msg.Channel.Id;
        SaveCommands();
        await msg.Channel.SendMessageAsync("🛠 Command channel set.");
    }

    private async Task SetAnnounceChannel(SocketUserMessage msg)
    {
        var user = msg.Author as SocketGuildUser;
        if (!IsAdmin(user)) return;

        _announceChannels[user.Guild.Id] = msg.Channel.Id;
        SaveAnnounce();
        await msg.Channel.SendMessageAsync("📢 Announcement channel set.");
    }

    // ================= STORAGE =================
    private void LoadData()
    {
        if (File.Exists(PointsFile))
            _points = JsonSerializer.Deserialize<Dictionary<ulong, int>>(File.ReadAllText(PointsFile)) ?? new();
        if (File.Exists(PointGiversFile))
            _pointGiverRoles = JsonSerializer.Deserialize<HashSet<ulong>>(File.ReadAllText(PointGiversFile)) ?? new();
        if (File.Exists(UpgradesFile))
            _upgradePaths = JsonSerializer.Deserialize<List<UpgradePath>>(File.ReadAllText(UpgradesFile)) ?? new();
        if (File.Exists(AnnounceFile))
            _announceChannels = JsonSerializer.Deserialize<Dictionary<ulong, ulong>>(File.ReadAllText(AnnounceFile)) ?? new();
        if (File.Exists(CommandFile))
            _commandChannels = JsonSerializer.Deserialize<Dictionary<ulong, ulong>>(File.ReadAllText(CommandFile)) ?? new();
    }

    public void SavePoints() => File.WriteAllText(PointsFile, JsonSerializer.Serialize(_points));
    public void SaveUpgrades() => File.WriteAllText(UpgradesFile, JsonSerializer.Serialize(_upgradePaths));
    public void SavePointGivers() => File.WriteAllText(PointGiversFile, JsonSerializer.Serialize(_pointGiverRoles));
    public void SaveAnnounce() => File.WriteAllText(AnnounceFile, JsonSerializer.Serialize(_announceChannels));
    public void SaveCommands() => File.WriteAllText(CommandFile, JsonSerializer.Serialize(_commandChannels));
}

// ================= SLASH COMMANDS =================
public class SlashCommands : InteractionModuleBase<SocketInteractionContext>
{
    private readonly Program _bot;

    public SlashCommands(Program bot)
    {
        _bot = bot;
    }

    [SlashCommand("points", "Show points")]
    public async Task Points(SocketGuildUser? user = null)
    {
        user ??= (SocketGuildUser)Context.User;
        await RespondAsync($"{user.Username} has {_bot._points.GetValueOrDefault(user.Id)} points.", ephemeral: true);
    }

    [SlashCommand("givepoints", "Give points")]
    public async Task GivePoints(SocketGuildUser user, int amount = 1)
    {
        if (!_bot.IsAdminOrPointGiver((SocketGuildUser)Context.User))
        {
            await RespondAsync("❌ No permission.", ephemeral: true);
            return;
        }

        _bot._points[user.Id] = _bot._points.GetValueOrDefault(user.Id) + amount;
        _bot.SavePoints();
        await _bot.RecalculateRolesAsync(user);
        await RespondAsync("✅ Points given.", ephemeral: true);
    }

    [SlashCommand("restorepoints", "Reset points")]
    public async Task Restore(SocketGuildUser user)
    {
        if (!_bot.IsAdminOrPointGiver((SocketGuildUser)Context.User))
        {
            await RespondAsync("❌ No permission.", ephemeral: true);
            return;
        }

        _bot._points[user.Id] = 0;
        _bot.SavePoints();
        await _bot.RecalculateRolesAsync(user);
        await RespondAsync("♻ Points reset.", ephemeral: true);
    }

    [SlashCommand("upgrade", "Create upgrade path")]
    public async Task Upgrade(SocketRole oldRole, SocketRole newRole, int points)
    {
        if (!_bot.IsAdminOrPointGiver((SocketGuildUser)Context.User))
        {
            await RespondAsync("❌ No permission.", ephemeral: true);
            return;
        }

        _bot._upgradePaths.RemoveAll(u => u.OldRoleId == oldRole.Id && u.NewRoleId == newRole.Id);
        _bot._upgradePaths.Add(new Program.UpgradePath { OldRoleId = oldRole.Id, NewRoleId = newRole.Id, RequiredPoints = points });
        _bot.SaveUpgrades();
        await RespondAsync("✅ Upgrade path set.", ephemeral: true);
    }

    [SlashCommand("removeupgrade", "Remove upgrade path")]
    public async Task RemoveUpgrade(SocketRole oldRole, SocketRole newRole)
    {
        if (!_bot.IsAdminOrPointGiver((SocketGuildUser)Context.User))
        {
            await RespondAsync("❌ No permission.", ephemeral: true);
            return;
        }

        _bot._upgradePaths.RemoveAll(u => u.OldRoleId == oldRole.Id && u.NewRoleId == newRole.Id);
        _bot.SaveUpgrades();
        await RespondAsync("✅ Upgrade removed.", ephemeral: true);
    }

    [SlashCommand("setcommandchannel", "Set command channel")]
    public async Task SetCommand()
    {
        var u = (SocketGuildUser)Context.User;
        if (!_bot.IsAdmin(u))
        {
            await RespondAsync("❌ Admins only.", ephemeral: true);
            return;
        }

        _bot._commandChannels[u.Guild.Id] = Context.Channel.Id;
        _bot.SaveCommands();
        await RespondAsync("🛠 Command channel set.", ephemeral: true);
    }

    [SlashCommand("setannouncechannel", "Set announce channel")]
    public async Task SetAnnounce()
    {
        var u = (SocketGuildUser)Context.User;
        if (!_bot.IsAdmin(u))
        {
            await RespondAsync("❌ Admins only.", ephemeral: true);
            return;
        }

        _bot._announceChannels[u.Guild.Id] = Context.Channel.Id;
        _bot.SaveAnnounce();
        await RespondAsync("📢 Announcement channel set.", ephemeral: true);
    }

    [SlashCommand("addpointgiver", "Add point giver role")]
    public async Task AddPointGiver(SocketRole role)
    {
        var u = (SocketGuildUser)Context.User;
        if (!_bot.IsAdmin(u))
        {
            await RespondAsync("❌ Admins only.", ephemeral: true);
            return;
        }

        _bot._pointGiverRoles.Add(role.Id);
        _bot.SavePointGivers();
        await RespondAsync("✅ Point giver role added.", ephemeral: true);
    }

    [SlashCommand("showpoints", "Show leaderboard")]
    public async Task Leaderboard(SocketRole? role = null)
    {
        var u = (SocketGuildUser)Context.User;
        if (!_bot.IsAdminOrPointGiver(u))
        {
            await RespondAsync("❌ No permission.", ephemeral: true);
            return;
        }

        var users = Context.Guild.Users
            .Where(x => role == null || x.Roles.Any(r => r.Id == role.Id))
            .Select(u => new { u.Username, P = _bot._points.GetValueOrDefault(u.Id) })
            .OrderByDescending(x => x.P)
            .Take(10);

        await RespondAsync("**Leaderboard**\n" + string.Join("\n", users.Select((u, i) => $"{i + 1}. **{u.Username}** — {u.P}")), ephemeral: true);
    }

    [SlashCommand("help", "Show available commands")]
    public async Task Help()
    {
        var u = (SocketGuildUser)Context.User;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("**Discord Points Bot Commands**");
        sb.AppendLine("`/points [user]` - Show your or another user's points.");

        if (_bot.IsAdminOrPointGiver(u))
        {
            sb.AppendLine("`/givepoints <user> [amount]` - Give points.");
            sb.AppendLine("`/restorepoints <user>` - Reset points.");
            sb.AppendLine("`/upgrade <oldRole> <newRole> <points>` - Create upgrade path.");
            sb.AppendLine("`/removeupgrade <oldRole> <newRole>` - Remove upgrade path.");
            sb.AppendLine("`/showpoints [role]` - Leaderboard.");
        }

        if (_bot.IsAdmin(u))
        {
            sb.AppendLine("`/addpointgiver <role>` - Add a point giver role.");
            sb.AppendLine("`/setcommandchannel` - Set command channel.");
            sb.AppendLine("`/setannouncechannel` - Set announcement channel.");
        }

        await RespondAsync(sb.ToString(), ephemeral: true);
    }
}







