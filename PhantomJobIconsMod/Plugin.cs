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
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/pji";
    private const uint PhantomJobIconIdOffset = 82271;
    private const int ContentMemberRefreshSeconds = 15;
    private const float PhantomJobIconSize = 24f;
    private const float PhantomJobIconGap = 8f;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("PhantomJobIconsMod");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private readonly IReadOnlyDictionary<byte, ISharedImmediateTexture> phantomJobIcons;
    private readonly Dictionary<ulong, byte> jobsByRawContentId = new();
    private readonly Dictionary<ulong, byte> jobsByEntryContentId = new();
    private readonly HashSet<ulong> partyContentIds = new();
    private readonly List<PhantomJobIconDraw> phantomJobIconsToDraw = new();
    private DateTime nextContentMemberRequestUtc;
    private DateTime nextRuntimeDiagnosticsUtc;
    private string contentMemberRequestStatus = "not attempted";
    private string displayContext = "inactive";
    private int lastContentMemberEntryCount;
    private int lastRawMappedJobCount;
    private int lastEntryMappedJobCount;
    private int lastNamePlateHandlerCount;
    private int lastPlayerHandlerCount;
    private int lastMatchedPlayerCount;
    private int lastContentIdMatchCount;
    private int lastExcludedPlayerCount;
    private int lastVisibleNamePlateCount;
    private int lastQueuedIconCount;
    private int lastLocalStateJobId = -1;
    private int lastLocalRawJobId = -1;
    private int lastLocalEntryJobId = -1;
    private string lastJobMapSource = "unverified";

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

        nextContentMemberRequestUtc = now.AddSeconds(ContentMemberRefreshSeconds);
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
        jobsByRawContentId.Clear();
        jobsByEntryContentId.Clear();
        partyContentIds.Clear();
        lastNamePlateHandlerCount = handlers.Count;
        lastPlayerHandlerCount = 0;
        lastMatchedPlayerCount = 0;
        lastContentIdMatchCount = 0;
        lastExcludedPlayerCount = 0;
        lastVisibleNamePlateCount = 0;
        lastQueuedIconCount = 0;
        lastContentMemberEntryCount = 0;
        lastRawMappedJobCount = 0;
        lastEntryMappedJobCount = 0;
        lastLocalStateJobId = -1;
        lastLocalRawJobId = -1;
        lastLocalEntryJobId = -1;
        lastJobMapSource = "unverified";

        if (!Configuration.ShowPhantomJobs)
        {
            displayContext = "display disabled";
            return;
        }

        var occultCrescentState = PublicContentOccultCrescent.GetState();
        if (occultCrescentState == null)
        {
            displayContext = "outside Occult Crescent";
            return;
        }

        var localPlayerAddress = ObjectTable.LocalPlayer?.Address ?? IntPtr.Zero;
        var localCharacter = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)localPlayerAddress;
        var localContentId = localCharacter == null ? 0 : localCharacter->ContentId;
        for (var i = 0; i < PartyList.Length; i++)
        {
            var partyMember = PartyList[i];
            if (partyMember != null && partyMember.ContentId != 0)
                partyContentIds.Add(partyMember.ContentId);
        }

        if (localContentId != 0)
            partyContentIds.Add(localContentId);

        displayContext = "active";
        var contentMembers = InfoProxyContentMember.Instance();
        if (contentMembers == null || contentMembers->CharData == null)
        {
            lastContentMemberEntryCount = -1;
            return;
        }

        for (var i = 0; i < contentMembers->EntryCount && i < 200; i++)
        {
            var member = contentMembers->MemberData[i];
            if (!phantomJobIcons.ContainsKey(member.MKDSupportJob))
                continue;

            var rawContentId = contentMembers->CharData[i].ContentId;
            if (rawContentId != 0)
                jobsByRawContentId[rawContentId] = member.MKDSupportJob;

            var entryContentId = contentMembers->GetContentIdForEntry((uint)i);
            if (entryContentId != 0)
                jobsByEntryContentId[entryContentId] = member.MKDSupportJob;
        }

        lastLocalStateJobId = occultCrescentState->CurrentSupportJob;
        if (localContentId != 0)
        {
            if (jobsByRawContentId.TryGetValue(localContentId, out var localRawJobId))
                lastLocalRawJobId = localRawJobId;
            if (jobsByEntryContentId.TryGetValue(localContentId, out var localEntryJobId))
                lastLocalEntryJobId = localEntryJobId;
        }

        lastContentMemberEntryCount = (int)contentMembers->EntryCount;
        lastRawMappedJobCount = jobsByRawContentId.Count;
        lastEntryMappedJobCount = jobsByEntryContentId.Count;

        IReadOnlyDictionary<ulong, byte>? jobsByContentId = null;
        var rawMatchesLocal = lastLocalRawJobId >= 0 && lastLocalRawJobId == lastLocalStateJobId;
        var entryMatchesLocal = lastLocalEntryJobId >= 0 && lastLocalEntryJobId == lastLocalStateJobId;
        if (rawMatchesLocal && entryMatchesLocal)
        {
            var mapsAreEquivalent = jobsByRawContentId.Count == jobsByEntryContentId.Count;
            if (mapsAreEquivalent)
            {
                foreach (var entry in jobsByRawContentId)
                {
                    if (jobsByEntryContentId.TryGetValue(entry.Key, out var entryJobId) &&
                        entryJobId == entry.Value)
                        continue;

                    mapsAreEquivalent = false;
                    break;
                }
            }

            if (mapsAreEquivalent)
            {
                jobsByContentId = jobsByRawContentId;
                lastJobMapSource = "both";
            }
            else
            {
                lastJobMapSource = "ambiguous";
            }
        }
        else if (rawMatchesLocal)
        {
            jobsByContentId = jobsByRawContentId;
            lastJobMapSource = "raw";
        }
        else if (entryMatchesLocal)
        {
            jobsByContentId = jobsByEntryContentId;
            lastJobMapSource = "entry";
        }

        if (jobsByContentId == null)
        {
            displayContext = "job mapping unverified";
            return;
        }

        foreach (var handler in handlers)
        {
            var player = handler.PlayerCharacter;
            if (player == null)
                continue;

            lastPlayerHandlerCount++;
            var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;
            var contentId = character == null ? 0 : character->ContentId;

            if (partyContentIds.Contains(contentId))
            {
                lastExcludedPlayerCount++;
                continue;
            }

            if (!jobsByContentId.TryGetValue(contentId, out var jobId))
                continue;

            lastContentIdMatchCount++;
            lastMatchedPlayerCount++;

            var plate = (AddonNamePlate.NamePlateObject*)handler.NamePlateObjectAddress;
            if (plate == null || plate->RootComponentNode == null || !plate->IsVisible ||
                !plate->IsPlayerCharacter || plate->NameText == null ||
                !plate->NameText->IsVisible())
                continue;

            lastVisibleNamePlateCount++;
            var nameNode = &plate->NameText->AtkResNode;
            var scale = GetAccumulatedScale(nameNode);
            ushort textWidth = 0;
            ushort textHeight = 0;
            plate->NameText->GetTextDrawSize(&textWidth, &textHeight, null, 0, -1, false);

            NativeBounds bounds;
            nameNode->GetBounds(&bounds);
            var left = MathF.Min(bounds.Pos1.X, bounds.Pos2.X);
            var top = MathF.Min(bounds.Pos1.Y, bounds.Pos2.Y);
            var right = MathF.Max(bounds.Pos1.X, bounds.Pos2.X);
            var bottom = MathF.Max(bounds.Pos1.Y, bounds.Pos2.Y);

            var boxWidth = right - left;
            var drawnWidth = MathF.Min(textWidth * scale, boxWidth);
            var nameHeight = bottom - top;
            if (drawnWidth <= 0 || nameHeight <= 0)
                continue;

            var textLeft = left + (boxWidth - drawnWidth) / 2f;
            var iconMin = new Vector2(
                textLeft + (drawnWidth - PhantomJobIconSize) / 2f,
                top - PhantomJobIconGap - PhantomJobIconSize);
            phantomJobIconsToDraw.Add(new PhantomJobIconDraw(
                phantomJobIcons[jobId],
                iconMin,
                iconMin + new Vector2(PhantomJobIconSize)));
        }

        lastQueuedIconCount = phantomJobIconsToDraw.Count;
    }

    private static unsafe float GetAccumulatedScale(AtkResNode* node)
    {
        var scale = 1f;
        for (var current = node; current != null; current = current->ParentNode)
            scale *= current->ScaleX > 0f ? current->ScaleX : 1f;
        return scale;
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
            $"memberEntries={lastContentMemberEntryCount}, rawMappedJobs={lastRawMappedJobCount}, " +
            $"entryMappedJobs={lastEntryMappedJobCount}, jobMap={lastJobMapSource}, " +
            $"localStateJob={lastLocalStateJobId}, localRawJob={lastLocalRawJobId}, " +
            $"localEntryJob={lastLocalEntryJobId}, " +
            $"handlers={lastNamePlateHandlerCount}, playerHandlers={lastPlayerHandlerCount}, " +
            $"contentIdMatches={lastContentIdMatchCount}, excludedPartyPlayers={lastExcludedPlayerCount}, " +
            $"matchedPlayers={lastMatchedPlayerCount}, visibleNameplates={lastVisibleNamePlateCount}, " +
            $"queuedIcons={lastQueuedIconCount}, loadedTextures={loadedTextureCount}, textureErrors={failedTextureCount}.");
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
