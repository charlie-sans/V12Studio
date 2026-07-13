using System;
using V12.Core;
using Engine = twodog.Engine;
namespace V12StudioPak
{
    public class Program
    {
        public static void Main(string[] args)
        {
            GameRoot.ConfigureLogging();
            Console.WriteLine("V12TwoDog starting...");

            using var engine = new Engine("V12TwoDog", Engine.ResolveProjectDir(), "--gamepak=V12 Studio");
            using var godot = engine.Start();

            while (!godot.Iteration())
            {
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q)
                    break;
                Update(godot, engine);
            }

            Console.WriteLine("Shutting down...");
        }
        public static void Update(Godot.GodotInstance godot, twodog.Engine engine)
        {
            // Update logic here

        }
    }
}
