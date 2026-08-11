using System;
using V12.Core;
using V12TwoDog;
using V12Studio.Editor;
using V12.Components.Renderables;
using V12.Components;
using V12.Components.UI;

namespace V12StudioPak
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            GameRoot.ConfigureLogging();
            Console.WriteLine("Welcome to V12Studio");

            var editor = new Editor();
            using var host = new V12TwoDog.V12Host();
            
            host.Start(args: args);
            while (!host.Iteration())
            {

            }
        }
    }
}