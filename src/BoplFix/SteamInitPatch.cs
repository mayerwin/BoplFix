using HarmonyLib;
using Steamworks;

namespace BoplLanFix
{
    /// <summary>
    /// Runs immediately after the game initializes Steam (SteamManager.Awake calls
    /// SteamClient.Init, which is where Facepunch wires up SteamNetworkingUtils). At
    /// this point the networking interface is valid and the game has NOT yet created
    /// its listen socket / connections, so the global ICE config we apply here is
    /// inherited by every P2P connection the game makes afterwards.
    /// </summary>
    [HarmonyPatch(typeof(SteamClient), nameof(SteamClient.Init))]
    internal static class SteamClient_Init_Patch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Plugin.OnSteamInitialized();
        }
    }
}
