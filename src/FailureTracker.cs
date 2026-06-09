using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace LocalThemeExtractor
{
    /// <summary>
    /// Tracks per-item extraction failures in a persistent JSON file.
    /// Items that fail >= MaxRetries times are permanently skipped.
    /// Format: one JSON object per line — { "key": "...", "count": N, "last": "..." }
    /// </summary>
    internal class FailureTracker
    {
        private const string FileName = "lte-failures.json";
        private readonly string _filePath;
        private readonly int _maxRetries;
        private readonly Dictionary<string, int> _failures;

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
            if (!_failures.ContainsKey(key))
                _failures[key] = 0;
            _failures[key]++;
            Save();
            return _failures[key];
        }

        /// <summary>Clear failure record on success.</summary>
        public void RecordSuccess(string key)
        {
            if (_failures.Remove(key))
                Save();
        }

        public int GetFailureCount(string key)
        {
            return _failures.TryGetValue(key, out int c) ? c : 0;
        }

        // ── Persistence (simple line-based JSON) ─────────────────────────

        private Dictionary<string, int> Load()
        {
            var result = new Dictionary<string, int>();
            if (!File.Exists(_filePath)) return result;

            try
            {
                foreach (string line in File.ReadAllLines(_filePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // Parse: {"key":"...","count":N}
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
            try
            {
                var lines = new List<string>();
                foreach (var kv in _failures)
                {
                    // Escape key for JSON
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
