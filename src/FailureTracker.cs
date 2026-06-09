using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace LocalThemeExtractor
{
    /// <summary>
    /// Tracks per-item extraction failures in a persistent JSON file.
    /// Items that fail >= MaxRetries times are permanently skipped.
    /// Thread-safe for concurrent access from parallel tasks.
    /// </summary>
    internal class FailureTracker
    {
        private const string FileName = "lte-failures.json";
        private readonly string _filePath;
        private readonly int _maxRetries;
        private readonly ConcurrentDictionary<string, int> _failures;
        private readonly object _saveLock = new object();

        public FailureTracker(int maxRetries = 3)
        {
            _maxRetries = maxRetries;
            _filePath = Path.Combine("/config/data", FileName);
            _failures = Load();
        }

        /// <summary>Has this item already failed too many times?</summary>
        public bool IsBlacklisted(string key)
        {
            return _failures.TryGetValue(key, out int count) && count >= _maxRetries;
        }

        /// <summary>Record a failure. Returns the new failure count.</summary>
        public int RecordFailure(string key)
        {
            int newCount = _failures.AddOrUpdate(key, 1, (_, old) => old + 1);
            Save();
            return newCount;
        }

        /// <summary>Clear failure record on success.</summary>
        public void RecordSuccess(string key)
        {
            if (_failures.TryRemove(key, out _))
                Save();
        }

        // ── Persistence (simple line-based JSON) ─────────────────────────

        private ConcurrentDictionary<string, int> Load()
        {
            var result = new ConcurrentDictionary<string, int>();
            if (!File.Exists(_filePath)) return result;

            try
            {
                foreach (string line in File.ReadAllLines(_filePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var keyMatch = Regex.Match(line, "\"key\"\\s*:\\s*\"([^\"]+)\"");
                    var countMatch = Regex.Match(line, "\"count\"\\s*:\\s*(\\d+)");
                    if (keyMatch.Success && countMatch.Success)
                    {
                        string key = keyMatch.Groups[1].Value;
                        int count = int.Parse(countMatch.Groups[1].Value);
                        result[key] = count;
                    }
                }
            }
            catch { }
            return result;
        }

        private void Save()
        {
            lock (_saveLock)
            {
                try
                {
                    var lines = new List<string>();
                    foreach (var kv in _failures)
                    {
                        string escaped = kv.Key.Replace("\\", "\\\\").Replace("\"", "\\\"");
                        lines.Add(string.Format("{{\"key\":\"{0}\",\"count\":{1}}}", escaped, kv.Value));
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(_filePath));
                    File.WriteAllLines(_filePath, lines);
                }
                catch { }
            }
        }
    }
}
