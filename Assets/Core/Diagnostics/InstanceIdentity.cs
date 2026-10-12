using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Core.Diagnostics
{
    /// <summary>
    /// Tells running copies of the game apart when several run on one machine - the main Editor,
    /// Multiplayer Play Mode virtual players and builds - so each can sign in as its own player
    /// and write its own clearly named log.
    ///
    /// Sources:
    ///   -authProfile &lt;name&gt;   command-line argument for builds (or any Editor), e.g. "host", "client2"
    ///   -name &lt;PlayerN&gt;        passed by Multiplayer Play Mode to each virtual player ("Player1" = main Editor)
    /// With neither, the instance is the "main" one and uses the default Authentication profile,
    /// so a normal single-copy run keeps the same player ID it always had.
    /// </summary>
    public static class InstanceIdentity
    {
        private const string VirtualPlayerFolder = "/Library/VP/";
        private const int MaxProfileLength = 30; // Authentication service limit

        /// <summary>The value after a command-line flag (case-insensitive), or null.</summary>
        public static string CommandLineValue(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        /// <summary>Multiplayer Play Mode's name for this player ("Player2"...), or null outside MPPM.</summary>
        public static string PlayModePlayerName
        {
            get
            {
                if (!Application.isEditor) return null;
                var name = CommandLineValue("-name");
                if (!string.IsNullOrEmpty(name)) return name;
                if (!IsVirtualPlayer) return null;

                // No -name argument: fall back to the clone's folder name, which is unique per virtual player.
                var data = Application.dataPath.Replace('\\', '/');
                var after = data.Substring(data.IndexOf(VirtualPlayerFolder, StringComparison.Ordinal) + VirtualPlayerFolder.Length);
                var slash = after.IndexOf('/');
                return "VP" + (slash > 0 ? after.Substring(0, slash) : after);
            }
        }

        /// <summary>True inside a Multiplayer Play Mode virtual player (a clone Editor under Library/VP).</summary>
        public static bool IsVirtualPlayer =>
            Application.isEditor && Application.dataPath.Replace('\\', '/').Contains(VirtualPlayerFolder);

        /// <summary>
        /// The Authentication profile to sign in with, from -authProfile, or null to let the
        /// Authentication package choose. (In virtual players it already picks the -name value,
        /// e.g. "Player2", so they get separate player IDs without help from us.)
        /// </summary>
        public static string AuthProfile
        {
            get
            {
                var explicitProfile = CommandLineValue("-authProfile");
                return string.IsNullOrEmpty(explicitProfile) ? null : Sanitize(explicitProfile);
            }
        }

        /// <summary>Short label for log file names: the -authProfile, the virtual player's name, or "main".</summary>
        public static string Label => AuthProfile ?? Sanitize(PlayModePlayerName ?? "") ?? "main";

        /// <summary>
        /// The real project folder in the Editor, even inside a virtual player (whose dataPath points
        /// into Library/VP/&lt;id&gt;/Assets), so every instance's logs land in one place.
        /// </summary>
        public static string EditorProjectRoot
        {
            get
            {
                var data = Application.dataPath.Replace('\\', '/');
                var vp = data.IndexOf(VirtualPlayerFolder, StringComparison.Ordinal);
                var root = vp >= 0 ? data.Substring(0, vp) : Path.GetDirectoryName(data);
                return Path.GetFullPath(root);
            }
        }

        /// <summary>Profile names allow letters, digits, '-' and '_' up to 30 characters.</summary>
        public static string Sanitize(string raw)
        {
            var clean = new StringBuilder();
            foreach (var c in raw.Where(c => char.IsLetterOrDigit(c) && c < 128 || c == '-' || c == '_'))
            {
                if (clean.Length == MaxProfileLength) break;
                clean.Append(c);
            }
            return clean.Length > 0 ? clean.ToString() : null;
        }
    }
}
