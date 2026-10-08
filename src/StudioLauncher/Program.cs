using System;
using V12.Core;
using V12TwoDog;
using V12Studio;

namespace V12Studio.Launcher
{
    /// <summary>
    /// Boots V12 Studio by starting the V12 host and pumping its frame loop.
    /// The studio itself (V12StudioPak) is renderer-agnostic and only exposes
    /// an IV12Gamepack; this launcher is the renderer-aware entry point.
    /// </summary>
    public static class StudioLauncher
    {
        [STAThread]
        public static void Main(string[] args)
        {
            GameRoot.ConfigureLogging();
            Console.WriteLine("Welcome to V12Studio");

            // Explicit boot, mirroring StudioLauncher.Monogame
            // (BasicRegistry.RegisterAll + LoadGamepacksFromAssembly): on nova
            // the pak assembly is picked up by RootLoop.DiscoverHostGamepaks,
            // which scans THIS entry assembly's direct references. That only
            // works if the compiler keeps the V12StudioPak reference in our
            // metadata — with no code touching it, the reference is dropped
            // and discovery silently falls back to the Sample Game. Touching
            // the pak type below (and logging it, so the use can't be trimmed)
            // keeps the reference alive and names the pak we intend to boot.
            var studioPakAssembly = typeof(V12StudioGamepack).Assembly;
            Console.WriteLine($"[StudioLauncher] Booting '{studioPakAssembly.GetName().Name}' on nova...");

            using var host = new V12Host();
            
            host.Start(args: args);
            while (!host.Iteration())
            {
            }
        }
    }
}
