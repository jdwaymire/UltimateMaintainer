// Common.cs
//
// Small pieces every job shares: the log and JSON interfaces (Emby's versions are behind them in Emby/), and
// Selection, the "all, or only these ids" setting used for users and libraries.
//
// NOTE: Emby user ids come in two spellings (with and without dashes, upper or lower case) depending on which API
// handed them out. Selection compares them without dashes and case, so a saved id always matches.

using System;
using System.Collections.Generic;
using System.Linq;

namespace UltimateMaintainer.Core
{
    public interface IRunLog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    public interface IJson
    {
        T Deserialize<T>(string json);
        string Serialize(object value);
    }

    // "all" (or empty) = everything, including things added later; otherwise a comma-separated list of ids.
    public class Selection
    {
        public bool All { get; private set; }
        public HashSet<string> Ids { get; private set; } = new HashSet<string>();

        public static Selection Parse(string text)
        {
            var ids = (text ?? "").Split(',').Select(Normalize).Where(s => s.Length > 0).ToList();
            if (ids.Count == 0 || ids.Contains("all"))
                return new Selection { All = true };
            return new Selection { Ids = new HashSet<string>(ids) };
        }

        public bool Contains(string id) => All || (id != null && Ids.Contains(Normalize(id)));

        public override string ToString() => All ? "all" : string.Join(",", Ids);

        static string Normalize(string id) => id.Trim().Replace("-", "").ToLowerInvariant();
    }
}
