using System;
using System.IO;

namespace AssetExport
{
    internal static class Program
    {
        /// <summary>AssetExport [output folder] (default: assets\discord under the current directory).</summary>
        private static int Main(string[] args)
        {
            var folder = args.Length > 0 ? args[0] : Path.Combine("assets", "discord");
            foreach (var path in DdoDiscordPresence.UI.PresenceIcon.ExportAll(folder))
                Console.WriteLine("wrote " + Path.GetFileName(path));
            return 0;
        }
    }
}
