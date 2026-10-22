using System;
using V12.Basic;
using V12.Core;
// using V12.Monogame;

namespace V12Studio.Launcher
{
    /// <summary>
    /// Launches V12 Studio on the MonoGame renderer. The studio itself is
    /// renderer-agnostic; this host only adapts it to MonoGame.
    /// </summary>
    public static class StudioLauncherMonogame
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // GameRoot.ConfigureLogging();
            // Console.WriteLine("Welcome to V12Studio (MonoGame renderer)");

            // MonogameLauncher.Run(root =>
            // {
            //     BasicRegistry.RegisterAll(root);
            //     root.LoadGamepacksFromAssembly(typeof(V12StudioGamepack).Assembly);
            // });
        }
    }
}
