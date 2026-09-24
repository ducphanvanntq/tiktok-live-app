using UnityEngine;

namespace TikTokLiveGame
{
    public static class RuntimeBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartGame()
        {
            if (Object.FindFirstObjectByType<TikTokGameController>() != null) return;
            string welcomeDirectory = VisualCaptureHarness.ParseWelcomeDirectory(System.Environment.GetCommandLineArgs(), out string argumentError);
            if (argumentError != null)
            {
                Debug.LogError(argumentError);
                Application.Quit(2);
                return;
            }
            // TikTok LIVE Studio captures at 60 FPS. Disable display-rate VSync so
            // high-refresh monitors cannot force uneven 60 FPS capture cadence.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            Application.runInBackground = true;
            Screen.SetResolution(540, 960, false);
            ClubSceneBuilder.Build();

            GameObject root = new("TikTok Live Game");
            AvatarService avatars = root.AddComponent<AvatarService>();
            bool ledDemo = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-ledFloorDemo") >= 0;
            TikTokWebSocketClient client = welcomeDirectory == null && !ledDemo ? root.AddComponent<TikTokWebSocketClient>() : null;
            PlayerManager players = new GameObject("Players").AddComponent<PlayerManager>();
            players.transform.SetParent(root.transform);
            GiftEffectManager giftEffects = root.AddComponent<GiftEffectManager>();
            root.AddComponent<MusicPlaylistPlayer>();
            TikTokGameController game = root.AddComponent<TikTokGameController>();
            game.Initialize(client, players, giftEffects);
            VisualCaptureHarness.InstallIfRequested(root, welcomeDirectory);
            StageLightingCaptureHarness.InstallIfRequested(root);
        }
    }
}
