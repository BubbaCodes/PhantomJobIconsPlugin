using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.IoC;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using PhantomJobIconsMod.Windows;
using NativeBounds = FFXIVClientStructs.FFXIV.Common.Math.Bounds;

namespace PhantomJobIconsMod;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static INamePlateGui NamePlateGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/pji";
    private const uint PhantomJobIconIdOffset = 82271;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("PhantomJobIconsMod");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private readonly IReadOnlyDictionary<byte, ISharedImmediateTexture> phantomJobIcons;
    private readonly Dictionary<uint, byte> jobsByEntityId = new();
    private readonly List<PhantomJobIconDraw> phantomJobIconsToDraw = new();
    private DateTime nextContentMemberRequestUtc;
    private DateTime nextRuntimeDiagnosticsUtc;
    private string contentMemberRequestStatus = "not attempted";
    private string displayContext = "inactive";
    private int lastContentMemberEntryCount;
    private int lastMappedJobCount;
    private int lastNamePlateHandlerCount;
    private int lastPlayerHandlerCount;
    private int lastMatchedPlayerCount;
    private int lastVisibleNamePlateCount;
    private int lastQueuedIconCount;

    private readonly record struct PhantomJobIconDraw(ISharedImmediateTexture Texture, Vector2 Min, Vector2 Max);

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        phantomJobIcons = DataManager.GetExcelSheet<MKDSupportJob>()
            .ToDictionary(
                job => (byte)job.RowId,
                job => TextureProvider.GetFromGameIcon(new GameIconLookup(job.RowId + PhantomJobIconIdOffset, false, true)));

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Phantom Job Icons window."
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw += DrawPhantomJobIcons;
        Framework.Update += OnFrameworkUpdate;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        NamePlateGui.OnDataUpdate += OnNamePlateDataUpdate;
        NamePlateGui.OnPostDataUpdate += OnPostNamePlateDataUpdate;

        // Add a simple message to the log with level set to information
        // Use /xllog to open the log window in-game
        // Example Output: 00:57:54.959 | INF | [PhantomJobIconsMod] ===A cool log message from Phantom Job Icons Mod===
        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
    }

    public void Dispose()
    {
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw -= DrawPhantomJobIcons;
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        NamePlateGui.OnDataUpdate -= OnNamePlateDataUpdate;
        NamePlateGui.OnPostDataUpdate -= OnPostNamePlateDataUpdate;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        // In response to the slash command, toggle the display status of our main ui
        MainWindow.Toggle();
    }

    private unsafe void OnFrameworkUpdate(IFramework _)
    {
        if (!Configuration.ShowPhantomJobs || PublicContentOccultCrescent.GetState() == null)
        {
            nextContentMemberRequestUtc = DateTime.MinValue;
            contentMemberRequestStatus = Configuration.ShowPhantomJobs
                ? "outside Occult Crescent"
                : "display disabled";
            return;
        }

        var now = DateTime.UtcNow;
        if (now < nextContentMemberRequestUtc)
            return;

        nextContentMemberRequestUtc = now.AddSeconds(60);
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
        {
            contentMemberRequestStatus = "agent module unavailable";
            return;
        }

        var agent = agentModule->GetAgentByInternalId(AgentId.ContentMemberList);
        if (agent == null)
        {
            contentMemberRequestStatus = "content-member agent unavailable";
            return;
        }

        if (agent->IsAgentActive())
        {
            contentMemberRequestStatus = "content-member agent active; request skipped";
            return;
        }

        var returnValue = stackalloc AtkValue[1];
        var arguments = stackalloc AtkValue[1];
        arguments[0].SetInt(1);
        agent->ReceiveEvent(returnValue, arguments, 1, 0);
        contentMemberRequestStatus = "request sent";
    }

    private unsafe void OnPostNamePlateDataUpdate(
        INamePlateUpdateContext _,
        IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        phantomJobIconsToDraw.Clear();
        jobsByEntityId.Clear();
        lastNamePlateHandlerCount = handlers.Count;
        lastPlayerHandlerCount = 0;
        lastMatchedPlayerCount = 0;
        lastVisibleNamePlateCount = 0;
        lastQueuedIconCount = 0;
        lastContentMemberEntryCount = 0;
        lastMappedJobCount = 0;

        if (!Configuration.ShowPhantomJobs)
        {
            displayContext = "display disabled";
            return;
        }

        if (PublicContentOccultCrescent.GetState() == null)
        {
            displayContext = "outside Occult Crescent";
            return;
        }

        displayContext = "active";
        var contentMembers = InfoProxyContentMember.Instance();
        if (contentMembers == null)
        {
            lastContentMemberEntryCount = -1;
            return;
        }

        // The Occult Crescent member list maps each actor entity ID to its selected Phantom Job.
        for (var i = 0; i < contentMembers->EntryCount && i < 200; i++)
        {
            var member = contentMembers->MemberData[i];
            if (member.EntityId != 0 && phantomJobIcons.ContainsKey(member.MKDSupportJob))
                jobsByEntityId[member.EntityId] = member.MKDSupportJob;
        }

        lastContentMemberEntryCount = (int)contentMembers->EntryCount;
        lastMappedJobCount = jobsByEntityId.Count;

        foreach (var handler in handlers)
        {
            var player = handler.PlayerCharacter;
            if (player == null)
                continue;

            lastPlayerHandlerCount++;
            if (!jobsByEntityId.TryGetValue(player.EntityId, out var jobId))
                continue;

            lastMatchedPlayerCount++;
            var plate = (AddonNamePlate.NamePlateObject*)handler.NamePlateObjectAddress;
            if (plate == null || plate->RootComponentNode == null || !plate->IsVisible ||
                !plate->IsPlayerCharacter || plate->NameText == null ||
                !plate->NameText->IsVisible())
                continue;

            lastVisibleNamePlateCount++;
            var nameNode = &plate->NameText->AtkResNode;
            NativeBounds bounds;
            nameNode->GetBounds(&bounds);

            var left = MathF.Min(bounds.Pos1.X, bounds.Pos2.X);
            var top = MathF.Min(bounds.Pos1.Y, bounds.Pos2.Y);
            var right = MathF.Max(bounds.Pos1.X, bounds.Pos2.X);
            var bottom = MathF.Max(bounds.Pos1.Y, bounds.Pos2.Y);
            var nameHeight = bottom - top;
            if (right <= left || nameHeight <= 0)
                continue;

            var iconSize = MathF.Min(20f, nameHeight);
            var iconMin = new Vector2(right + 3f, top + (nameHeight - iconSize) / 2f);
            phantomJobIconsToDraw.Add(new PhantomJobIconDraw(
                phantomJobIcons[jobId],
                iconMin,
                iconMin + new Vector2(iconSize)));
        }

        lastQueuedIconCount = phantomJobIconsToDraw.Count;
    }

    // Subscribing here makes Dalamud invoke the post-update callback for all active nameplates each frame.
    private static void OnNamePlateDataUpdate(
        INamePlateUpdateContext _,
        IReadOnlyList<INamePlateUpdateHandler> __)
    {
    }

    private void DrawPhantomJobIcons()
    {
        var drawList = ImGui.GetForegroundDrawList();
        var logDiagnostics = DateTime.UtcNow >= nextRuntimeDiagnosticsUtc;
        var loadedTextureCount = 0;
        var failedTextureCount = 0;

        foreach (var icon in phantomJobIconsToDraw)
        {
            if (logDiagnostics)
            {
                if (icon.Texture.TryGetWrap(out var loadedTexture, out var exception) && loadedTexture is not null)
                    loadedTextureCount++;
                else if (exception is not null)
                    failedTextureCount++;
            }

            drawList.AddImage(icon.Texture.GetWrapOrEmpty().Handle, icon.Min, icon.Max);
        }

        if (!logDiagnostics)
            return;

        nextRuntimeDiagnosticsUtc = DateTime.UtcNow.AddSeconds(30);
        Log.Information(
            $"Runtime diagnostics: context={displayContext}, request={contentMemberRequestStatus}, " +
            $"memberEntries={lastContentMemberEntryCount}, mappedJobs={lastMappedJobCount}, " +
            $"handlers={lastNamePlateHandlerCount}, playerHandlers={lastPlayerHandlerCount}, " +
            $"entityMatches={lastMatchedPlayerCount}, visibleNameplates={lastVisibleNamePlateCount}, " +
            $"queuedIcons={lastQueuedIconCount}, loadedTextures={loadedTextureCount}, textureErrors={failedTextureCount}.");
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
