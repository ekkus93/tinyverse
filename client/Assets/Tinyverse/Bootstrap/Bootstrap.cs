using System.Collections;
using System.Globalization;
using System.IO;
using Tinyverse.UI;
using UnityEngine;

namespace Tinyverse.Bootstrap
{
    /// <summary>
    /// The only scene object. Creates the <see cref="App"/>, runs it every frame (before the avatars and
    /// the UI, so network events land first), acts on the command line and shuts down cleanly on quit.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class Bootstrap : MonoBehaviour
    {
        App app;
        CommandLine args;
        float nextShot;
        int shotCount;
        bool touring;

        public App App => app;

        void Awake()
        {
            Application.targetFrameRate = 120;
            args = CommandLine.FromEnvironment();
            Debug.Log("[Bootstrap] Tinyverse " + Application.version);
            app = new App(args, transform);
            Application.quitting += OnQuitting;
            if (!string.IsNullOrEmpty(args.ScreenshotDirectory))
            {
                args.ScreenshotDirectory = Path.GetFullPath(args.ScreenshotDirectory);
                Directory.CreateDirectory(args.ScreenshotDirectory);
                nextShot = args.ScreenshotEvery > 0 ? args.ScreenshotEvery : float.MaxValue;
            }
        }

        IEnumerator Start()
        {
            if (args.ScreenshotTour && args.ScreenshotDirectory != null)
            {
                touring = true;
                yield return new WaitForSecondsRealtime(1.5f);
                yield return Shot("connect");
                app.ShowHostScreen();
                yield return new WaitForSecondsRealtime(0.8f);
                yield return Shot("host");
                app.UI.ShowScreen(UIScreen.Connect);
                yield return new WaitForSecondsRealtime(0.3f);
                touring = false;
            }

            if (!string.IsNullOrEmpty(args.HostWorldDirectory))
                app.HostFromCommandLine(args.HostWorldDirectory, args.Port);
            else if (!string.IsNullOrEmpty(args.ConnectAddress))
                app.Connect(args.ConnectAddress, string.IsNullOrEmpty(args.Name) ? "Visitor" : args.Name, args.Password ?? "");

            if (args.ScreenshotTour && args.ScreenshotDirectory != null)
            {
                // In-world panels, once the world is ready.
                float waited = 0f;
                while ((app.Session == null || !app.Session.SnapshotComplete) && waited < 30f)
                {
                    waited += Time.unscaledDeltaTime;
                    yield return null;
                }
                if (app.Session == null) yield break;
                yield return new WaitForSecondsRealtime(4f);
                touring = true;
                app.UI.OpenMenu();
                yield return new WaitForSecondsRealtime(0.6f);
                yield return Shot("menu");
                app.UI.OpenSettings();
                yield return new WaitForSecondsRealtime(0.6f);
                yield return Shot("settings");
                app.UI.Settings.Close();
                app.UI.CloseMenu();
                app.UI.TogglePeople();
                yield return new WaitForSecondsRealtime(0.6f);
                yield return Shot("people");
                app.UI.TogglePeople();
                touring = false;
            }
        }

        IEnumerator Shot(string label)
        {
            yield return new WaitForEndOfFrame();
            shotCount++;
            string file = Path.Combine(args.ScreenshotDirectory,
                string.Format(CultureInfo.InvariantCulture, "shot-{0:000}-{1}-{2}x{3}.png", shotCount, label, Screen.width, Screen.height));
            ScreenCapture.CaptureScreenshot(file);
            Debug.Log("[Bootstrap] screenshot " + file);
        }

        void Update()
        {
            app.Tick(Time.deltaTime);

            float t = Time.realtimeSinceStartup;
            if (args.ScreenshotDirectory != null && t >= nextShot && !touring)
            {
                nextShot = t + args.ScreenshotEvery;
                StartCoroutine(Shot(Label()));
            }
            if (args.QuitAfter > 0f && t >= args.QuitAfter)
            {
                args.QuitAfter = 0f;
                Debug.Log("[Bootstrap] --quit-after reached");
                app.Quit();
            }
        }

        void LateUpdate() => app.LateTick();

        string Label()
        {
            switch (app.State)
            {
                case AppState.InWorld:
                    return app.Session != null && app.Session.BuildMode ? "build" : "world";
                default:
                    return app.State.ToString().ToLowerInvariant();
            }
        }

        void OnQuitting()
        {
            Application.quitting -= OnQuitting;
            app?.Shutdown();
        }

        void OnDestroy()
        {
            Application.quitting -= OnQuitting;
            app?.Dispose();
            app = null;
        }
    }
}
