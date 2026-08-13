using Cuckoo.Models;

namespace Cuckoo.Core;

/// <summary>
/// The surface the mining client uses to talk to the UI.
/// Mirrors the responsibilities of GUIManager in the original miner.
/// All members must be safe to call from background threads.
/// </summary>
public interface IMinerGui
{
    bool CloseRequested { get; }

    // Output / status
    void Print(string message);
    void SetStatus(string status);
    void UpdateWebsocketStatus(int index, string? status, int? topics);
    void RemoveWebsocketStatus(int index);

    // Campaign progress panel
    void DisplayDrop(TimedDrop? drop, bool countdown = true, bool subone = false);
    void ClearDrop();
    bool MinuteAlmostDone();
    void StopTimer();

    // Login form
    void ShowDeviceCode(string verificationUri, string userCode);
    void LoginUpdate(string status, long? userId = null, string? userName = null);

    // Channel list
    void AddChannel(Channel channel);
    void ClearChannels();
    void RemoveChannel(Channel channel);
    void SetWatching(Channel channel);
    void ClearWatching();
    Channel? GetSelectedChannel();
    void ClearSelectedChannel();

    // Inventory / settings
    void ClearInventory();
    void AddCampaigns(IReadOnlyList<DropsCampaign> campaigns);
    void SetGames(IReadOnlyCollection<Game> games, IReadOnlySet<string> linkedGameNames);

    // Tray
    void TrayNotify(string message, string title);
    void ChangeTrayIcon(string state);
}
